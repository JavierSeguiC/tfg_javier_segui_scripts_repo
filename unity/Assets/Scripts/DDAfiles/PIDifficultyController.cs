using System;
using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// MODEL-FREE PI DIFFICULTY CONTROLLER — the deliverable control layer.
    /// A complete alternative to RuleBasedDDAController; the two are mutually
    /// exclusive via DifficultyAuthority.
    ///
    /// This script does exactly two jobs and nothing else:
    ///   (1) STATE ESTIMATION — turn the raw NoteOutcomeEvent stream into the two
    ///       regulated signals: errors/min (scalar) and force margin M_F,ℓ (per lane).
    ///   (2) CONTROL — run five independent discrete PI loops and write the result
    ///       to GameDifficulty.Instance once per control tick.
    /// Visualisation, plotting and tuning UI live in PITuningHUD, not here.
    ///
    /// ── LOOP 1: REFLEX / TIMING ─────────────────────────────────────────────
    ///   manipulated : scalar general difficulty d ∈ [0,1]
    ///   controlled  : errors per minute (rolling estimate, see below)
    ///   reference   : targetErrorsPerMinute (default 10)
    ///   units       : d is a STEP COUNT, not a [0,1] fraction. One unit = one
    ///                 success's worth of movement, the SAME scale the rule-based
    ///                 controller steps in, so the two are directly comparable.
    ///                 d is clamped to mapping.[minDifficulty, maxDifficulty]
    ///                 (default 5..200).
    ///   actuators   : d drives BOTH note speed and spawn interval through
    ///                 DifficultyMapping — see that class for the full derivation.
    ///                 In brief: v(d) = speedStep·d is linear everywhere, while
    ///                 T(d) is clamped to [0.15, 2.0] s, which makes it flat →
    ///                 linear → flat. Outside the breakpoints (d=12 and d=123 at
    ///                 the defaults) the interval saturates and only speed keeps
    ///                 moving; between them the two move together locked at the
    ///                 hand-tuned ratio speedStep/intervalStep = 300 wu/s per
    ///                 second of interval. Fusing them onto one knob is what keeps
    ///                 this a SISO loop instead of an ill-conditioned 2×2.
    ///   NOTE: timing margin M_t is deliberately NOT regulated — it does not scale
    ///   linearly with the actuators, so it is a diagnostic only.
    ///
    /// ── LOOPS 2–5: FORCE, one per lane, INDEPENDENT ─────────────────────────
    ///   manipulated : τ_ℓ = GameDifficulty.requiredForce[ℓ], clamped [0.2, 0.8]
    ///   controlled  : force margin M_F,ℓ (EMA)
    ///   reference   : a small POSITIVE margin — the player just barely meets the
    ///                 requirement consistently
    ///   structure   : τ_ℓ affects only its own lane's margin, slope exactly −1,
    ///                 so the four loops decouple and each is a plain SISO PI with
    ///                 plantSign = −1.
    ///
    /// ── STATE ESTIMATION ────────────────────────────────────────────────────
    /// ERRORS/MIN — time-based exponential estimator over a point process.
    ///   Errors arrive at random times, so a fixed-α sample-indexed EMA would have
    ///   a meaning that drifts with the event rate. Instead the estimate decays
    ///   continuously and is kicked by each event:
    ///       between events :  rate ← rate · exp(−Δt/τ)
    ///       on each error  :  rate ← rate + 1/τ
    ///   Each event contributes a unit-area kernel (1/τ)·exp(−t/τ), so for a Poisson
    ///   process of rate λ the estimator is unbiased: E[rate] = λ. Reported ×60 as
    ///   errors/min. This is the time-indexed EMA option, exact for irregular
    ///   spacing, and it is a clean first-order lag — which is what makes the open
    ///   loop a textbook FOPDT and lets SIMC produce the starting gains.
    ///
    ///   Fractional noise on this estimate is ≈ 1/√(λτ). At 10 err/min with τ = 10 s
    ///   only ~1.7 events land per time constant, so expect ±75% jitter. That is
    ///   physics, not a bug: it is why Kp is modest and why there is no D term.
    ///
    ///   CHORD WEIGHTING (Aug 2026). Events are no longer unit-weight. A note that
    ///   failed as one of N simultaneous notes contributes 1/N instead of 1, so the
    ///   estimator tracks a WEIGHTED rate Σw/Δt. Rationale: playtesting showed
    ///   players clear notes individually at a difficulty where they drop one or
    ///   more members of a chord at that same difficulty. Unweighted, a single
    ///   4-note chord could inject 4 events inside a few frames — worth 4/τ against
    ///   a signal that normally sees ~1.7 events per time constant — so one chord
    ///   dominated the estimate and produced exactly the erratic bursts the loop
    ///   then chased. Weighted, a chord is worth at most one error's worth however
    ///   large it is, and partial success is credited: 3 of 4 failed costs 0.75.
    ///   The kernel shape and τ are untouched, so this is still the same
    ///   first-order lag — only the event areas differ. Toggle:
    ///   fractionalChordErrors (off = the old unit-weight behaviour).
    ///
    /// FORCE MARGIN — time-based EMA per lane, α = 1 − exp(−Δt/τ_F), NaN-skipped,
    ///   with a staleness guard so a lane that has received no notes recently does
    ///   not get regulated on an out-of-date measurement.
    ///
    /// ── EVENT SOURCE ────────────────────────────────────────────────────────
    /// DDAEventBus.OnNoteOutcome (Stream 1). Stream 2 carries only succeeded/failed
    /// booleans; Stream 1 carries the full classified outcome, the lane and
    /// forceMargin, all of which the estimator needs. Stream 1's deferral (grace +
    /// fusion windows) IS the transport delay θ the controller is designed around.
    /// </summary>
    [DisallowMultipleComponent]
    public class PIDifficultyController : MonoBehaviour, IDifficultyWriter
    {
        // ══════════════════════════════════════════════════════════════════
        //  INSPECTOR
        // ══════════════════════════════════════════════════════════════════

        [Header("── Activation ──")]
        [Tooltip("Claim control of GameDifficulty as soon as the scene starts. " +
                 "Doing so automatically silences RuleBasedDDAController and " +
                 "DifficultyPresetSwitcher.")]
        public bool claimAuthorityOnStart = true;

        [Tooltip("Control period Ts in seconds. Deliberately MUCH shorter than the " +
                 "closed-loop response (~30 s) so difficulty glides in many small " +
                 "corrections instead of lurching once per response time. 1 s is " +
                 "~1/10 of the estimator time constant — small enough to look " +
                 "continuous, large enough that each tick sees a fresh estimate.")]
        [Range(0.1f, 10f)] public float controlPeriod = 1.0f;

        [Tooltip("Seconds after activation during which the loops observe but do NOT " +
                 "write. Lets the estimator fill before the integrator starts acting " +
                 "on a cold, biased-low reading. ~1 estimator time constant.")]
        public float warmupSeconds = 10f;

        // ------------------------------------------------------------------
        [Header("── Loop 1: reflex / timing — setpoint ──")]
        [Tooltip("Reference r for the reflex loop, in errors per minute. " +
                 "This is the flow-state target: the rate of failure the player is " +
                 "held at. 10 /min was chosen as the design setpoint.")]
        public float targetErrorsPerMinute = 10f;

        [Header("── Loop 1: estimator ──")]
        [Tooltip("Time constant τ of the errors/min estimator, seconds. Shorter = " +
                 "faster but noisier; longer = smoother but adds lag to the plant the " +
                 "PI must stabilise. 10 s was chosen as the balance point. " +
                 "NOTE: this IS part of the plant — changing it invalidates the gains.")]
        public float errorRateTauSeconds = 10f;

        [Tooltip("Time constant τ of the M_t (timing margin) DIAGNOSTIC estimator, " +
                 "seconds. Independent of errorRateTauSeconds on purpose: M_t is not " +
                 "regulated and not part of the loop-1 plant model, so smoothing it " +
                 "differently doesn't invalidate any gains. Migrated from the retired " +
                 "PerformanceMonitor (Aug 2026), which used a note-indexed EMA — this " +
                 "is the same signal on the current time-indexed estimator instead.")]
        public float timingMarginTauSeconds = 10f;

        [Header("── Loop 1: which outcomes count as an error ──")]
        [Tooltip("Count Missed / EarlyPress / LatePress. This is the reflex loop's " +
                 "own failure mode and matches PerformanceMonitor's ė convention.")]
        public bool countTimingErrors = true;

        [Tooltip("Count WrongLane. OFF by default: wrong-lane is a COORDINATION " +
                 "failure belonging to the supervisory layer, not to d.")]
        public bool countWrongLaneErrors = false;

        [Tooltip("Count ForceInsufficient / UnderHeld. OFF by default: those are what " +
                 "the four force loops regulate via τ_ℓ. Feeding them to loop 1 as " +
                 "well would let two controllers fight over one symptom. " +
                 "(Recorded data shows that near 10 err/min essentially all errors " +
                 "are timing errors anyway, so this toggle barely moves the operating " +
                 "point — but it keeps the loops structurally decoupled.)")]
        public bool countForceErrors = false;

        [Header("── Loop 1: how much a chord member's failure counts ──")]
        [Tooltip("Charge each failed note in a chord 1/chordSize of an error instead of a " +
                 "full one. Playtesting showed players clear individual notes at a given " +
                 "difficulty but drop at least one member of a chord at that SAME " +
                 "difficulty, so an unweighted count lets one chord inject up to 4 errors " +
                 "within a few frames — a burst the τ=10 s estimator turns into a sharp " +
                 "spike, since 4 simultaneous events are worth 4/τ against a signal that " +
                 "only sees ~1.7 events per time constant at the setpoint. Weighting makes " +
                 "a chord contribute at most ONE error's worth no matter its size, and " +
                 "credits the notes the player DID get: fail 3 of 4 and the cost is 0.75, " +
                 "not 3. Standalone notes are unaffected (chordSize 1 → weight 1). " +
                 "Turn OFF to reproduce the pre-August-2026 unweighted behaviour.")]
        public bool fractionalChordErrors = true;

        [Header("── Loops 2–5: which outcomes may feed the force estimator ──")]
        [Tooltip("Feed M_F,ℓ on clean Hits. The baseline sample: the player met the " +
                 "requirement and we learn by how much.")]
        public bool forceFromHits = true;

        [Tooltip("Feed M_F,ℓ on ForceInsufficient / UnderHeld. ON by design: these are " +
                 "the force loop's OWN failure mode. Excluding them would leave the " +
                 "estimator seeing only presses that SUCCEEDED — a survivor bias that " +
                 "reads M_F,ℓ optimistic and ratchets τ_ℓ upward against a player who is " +
                 "in fact failing. Same class of error as the M_t survivor-bias fix.")]
        public bool forceFromForceFailures = true;

        [Tooltip("Feed M_F,ℓ on EarlyPress / LatePress. OFF by design: a press WAS " +
                 "matched so the force data is real, but the note failed on TIMING. " +
                 "Admitting them couples the reflex failure mode into the force channel, " +
                 "which is exactly what the five-independent-loops structure exists to " +
                 "prevent.")]
        public bool forceFromTimingFailures = false;

        [Tooltip("Feed M_F,ℓ on WrongLane. OFF by design: on a WrongLane resolution the " +
                 "fused press came from a DIFFERENT lane than ev.lane, so the sample " +
                 "would be filed against the wrong finger's loop entirely.")]
        public bool forceFromWrongLane = false;

        [Header("── Loop 1: PI gains ──")]
        [Tooltip("Seed values from SIMC on the FOPDT estimate, RESCALED to the new " +
                 "step-count difficulty units. The original estimate was K≈90 err/min " +
                 "per unit of NORMALISED d, where d:0→1 spanned 400 wu/s. Per wu/s that " +
                 "is 90/400 = 0.225, and one difficulty unit is speedStep = 5 wu/s, so " +
                 "K ≈ 1.125 err/min per difficulty unit. With τ≈10 s, θ≈8 s, τ_c=15 s: " +
                 "Kp = (1/K)·τ/(τ_c+θ) = 0.386,  Ti = min(τ, 4(τ_c+θ)) = 10 s. " +
                 "Tune Kp up for a faster loop, down for a calmer one. " +
                 "NOTE: K was estimated over the OLD speed range on keyboard recordings — " +
                 "treat 0.386 as a starting point and expect to re-tune, especially since " +
                 "the interval now saturates at both ends (where only speed moves, the " +
                 "true plant gain per difficulty unit is smaller).")]
        public PIRegulator reflexLoop = new PIRegulator
        {
            kp = 0.386f, ti = 10f, uMin = 5f, uMax = 200f, plantSign = +1f
        };

        [Header("── Loop 1: d → (noteSpeed, spawnInterval) mapping ──")]
        [Tooltip("This controller's OWN copy of the mapping. RuleBasedDDAController has a " +
                 "separate one — normally set them the same so the two controllers are " +
                 "comparable, but they can be tuned independently on purpose.")]
        public DifficultyMapping mapping = new DifficultyMapping();

        [Tooltip("Difficulty level the controller seizes when it takes authority, in " +
                 "difficulty units (successes). 60 = the tuned anchor (300 wu/s at 1.2 s) " +
                 "with the default mapping.")]
        public float initialDifficulty = 60f;

        // ------------------------------------------------------------------
        [Header("── Loops 2–5: force, per lane ──")]
        [Tooltip("Master switch. Turn OFF for keyboard sessions, where there is no " +
                 "force sensor and the loop would chase an unsatisfiable signal. " +
                 "When OFF the controller never touches GameDifficulty.requiredForce.")]
        public bool enableForceLoops = false;

        [Tooltip("Reference force margin — small and POSITIVE, so the player just " +
                 "barely meets the requirement consistently. Units are normalised " +
                 "sensor units [0,1], same scale as requiredForce.")]
        public float targetForceMargin = 0.05f;

        [Tooltip("Time constant of the per-lane force-margin EMA, seconds.")]
        public float forceMarginTauSeconds = 10f;

        [Tooltip("τ_ℓ each lane seizes when the force loops are (re)initialised.")]
        [Range(0f, 1f)] public float initialTau = 0.4f;

        [Tooltip("Lowest force threshold. A patient who cannot reach this is too " +
                 "impaired for the game — this is an exclusion criterion, not a " +
                 "control limit.")]
        [Range(0f, 1f)] public float minTau = 0.2f;
        [Tooltip("Highest force threshold. Leaves headroom below the sensor ceiling.")]
        [Range(0f, 1f)] public float maxTau = 0.8f;

        [Tooltip("Shared gains for all four force loops. The plant here is known by " +
                 "construction — τ_ℓ moves M_F,ℓ with slope exactly −1 and almost no " +
                 "lag — so these are near-pure integrator loops. plantSign is forced " +
                 "to −1 at runtime. Ti ≈ 15 s gives a calm ~15–30 s response.")]
        public PIRegulator forceLoopTemplate = new PIRegulator
        {
            kp = 0.15f, ti = 15f, uMin = 0.2f, uMax = 0.8f, plantSign = -1f
        };

        // ------------------------------------------------------------------
        [Header("── Output ──")]
        [Tooltip("Also mirror d into GameDifficulty.generalDifficulty (cosmetic only; " +
                 "same convention RuleBasedDDAController uses).")]
        public bool writeGeneralDifficulty = true;

        [Tooltip("Log one line per control tick to the Unity console. Noisy — for " +
                 "debugging only.")]
        public bool verboseLogging = false;

        // ══════════════════════════════════════════════════════════════════
        //  PUBLIC READ-OUTS (consumed by PITuningHUD — this script draws nothing)
        // ══════════════════════════════════════════════════════════════════

        public const int LaneCount = 4;

        public bool  IsActive          { get; private set; }
        public bool  IsWarmingUp       => IsActive && (Time.time - _activatedAt) < warmupSeconds;
        public float WarmupRemaining   => Mathf.Max(0f, warmupSeconds - (Time.time - _activatedAt));
        public float SecondsActive     => IsActive ? Time.time - _activatedAt : 0f;

        /// <summary>Current estimate of the regulated signal, errors per minute.</summary>
        public float ErrorsPerMinute   => _errorRate.PerMinute(Time.time);
        public float Setpoint          => targetErrorsPerMinute;
        public float Difficulty        => _d;
        public float NoteSpeed         => mapping.NoteSpeed(_d);
        public float SpawnInterval     => mapping.SpawnInterval(_d);
        /// <summary>d mapped to [0,1] across its clamp range — for plots only.</summary>
        public float DifficultyNormalised => mapping.Normalised(_d);
        /// <summary>True when spawn interval is pinned and only note speed is moving.</summary>
        public bool  IntervalSaturated  => mapping.IntervalSaturated(_d);
        public float SpawnFrequency    => SpawnInterval > 1e-4f ? 1f / SpawnInterval : 0f;
        /// <summary>Raw number of failed notes counted — UNWEIGHTED, one per note.</summary>
        public int   TotalErrorsCounted { get; private set; }
        /// <summary>
        /// Sum of the WEIGHTS actually fed to the estimator (Σ 1/chordSize). Equals
        /// TotalErrorsCounted when fractionalChordErrors is off or no chords spawned;
        /// the gap between the two is exactly how much the chord weighting removed.
        /// </summary>
        public float TotalErrorWeight   { get; private set; }
        public int   TotalNotesSeen     { get; private set; }
        public float LastTickDt         { get; private set; }

        /// <summary>Diagnostic only — timing margin M_t is NOT regulated.</summary>
        public float TimingMarginDiagnostic => _timingMargin.HasValue ? _timingMargin.Value : float.NaN;

        public float Tau(int lane)          => (lane >= 0 && lane < LaneCount) ? _tau[lane] : float.NaN;
        public float ForceMargin(int lane)  => (lane >= 0 && lane < LaneCount) ? _forceMargin[lane].Value : float.NaN;
        public PIRegulator ForceLoop(int l) => (l >= 0 && l < LaneCount) ? _forceLoops[l] : null;

        /// <summary>Fired after every control tick, for plotting / logging.</summary>
        public event Action OnControlTick;

        // ── IDifficultyWriter ──
        public string AuthorityName => "PI Difficulty Controller";

        // ══════════════════════════════════════════════════════════════════
        //  RUNTIME STATE
        // ══════════════════════════════════════════════════════════════════

        float _d;
        readonly float[] _tau = new float[LaneCount];
        readonly PIRegulator[] _forceLoops = new PIRegulator[LaneCount];
        readonly PointProcessRateEstimator _errorRate = new PointProcessRateEstimator();
        readonly TimeBasedEma[] _forceMargin = new TimeBasedEma[LaneCount];
        readonly TimeBasedEma _timingMargin = new TimeBasedEma();   // diagnostic only

        float _activatedAt;
        float _tickAccumulator;
        float _lastTickTime;
        readonly HashSet<int> _seenNotes = new HashSet<int>();

        // Outcome → classification, resolved once by NAME so this file does not
        // hard-depend on the exact members of the game-side NoteOutcome enum.
        enum OutcomeClass { Success, Timing, WrongLane, Force, Other }
        static Dictionary<NoteOutcome, OutcomeClass> _classification;

        // ══════════════════════════════════════════════════════════════════
        //  LIFECYCLE
        // ══════════════════════════════════════════════════════════════════

        void Awake()
        {
            BuildClassification();

            for (int l = 0; l < LaneCount; l++)
            {
                _forceMargin[l] = new TimeBasedEma();
                _forceLoops[l]  = CloneForceTemplate();
            }
            ResetInternalState();
            DifficultyAuthority.Register(this);
        }

        void OnEnable()  => DDAEventBus.OnNoteOutcome += OnNoteOutcome;
        void OnDisable() => DDAEventBus.OnNoteOutcome -= OnNoteOutcome;

        void OnDestroy() => DifficultyAuthority.Unregister(this);

        void Start()
        {
            // The menu bridge is the single place that decides the initial controller
            // (from the persisted 'dda.controller' setting), so defer to it when present
            // to avoid a startup claim race. claimAuthorityOnStart still applies in a
            // bridge-less scene — a controller-only test, or the delete-test with the menu
            // layer stripped — where this controller is the only writer around.
            if (claimAuthorityOnStart &&
                FindFirstObjectByType<MenuDDABridge>() == null)
                DifficultyAuthority.Claim(this);
        }

        void Update()
        {
            if (!IsActive || !DifficultyAuthority.HasAuthority(this)) return;

            _tickAccumulator += Time.deltaTime;
            if (_tickAccumulator < controlPeriod) return;

            float now = Time.time;
            float dt  = now - _lastTickTime;      // ACTUAL elapsed, not nominal
            _tickAccumulator = 0f;
            _lastTickTime    = now;
            LastTickDt       = dt;

            Tick(dt, now);
        }

        // ══════════════════════════════════════════════════════════════════
        //  AUTHORITY
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Take control of GameDifficulty (revokes every other writer).</summary>
        public void Activate() => DifficultyAuthority.Claim(this);

        /// <summary>Give up control. The game keeps whatever values were last written.</summary>
        public void Deactivate() => DifficultyAuthority.Release(this);

        public void OnAuthorityGranted()
        {
            ResetInternalState();
            IsActive      = true;
            _activatedAt  = Time.time;
            _lastTickTime = Time.time;
            _tickAccumulator = 0f;
            Apply();                                   // seize the initial operating point
        }

        public void OnAuthorityRevoked()
        {
            IsActive = false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  STATE ESTIMATION — the only place raw events are touched
        // ══════════════════════════════════════════════════════════════════

        void OnNoteOutcome(NoteOutcomeEvent ev)
        {
            if (ev == null) return;

            // The estimator runs even while inactive/warming up, so that whenever the
            // controller does take over, the signal is already converged.
            if (!_seenNotes.Add(ev.noteId)) return;      // one count per note
            TotalNotesSeen++;

            float now = Time.time;
            var cls = Classify(ev.outcome);

            if (CountsAsError(cls))
            {
                // A note failed as one of N simultaneous notes is weaker evidence than a
                // note failed alone: the player was asked for N things at once and may
                // well have got some of them. Charge 1/N of an error so the whole chord
                // is worth at most one error's worth, and so partial success is credited.
                // Applied UNCONDITIONALLY of the stagger — a chord is treated as a chord
                // whatever chordStaggerEighths says. (The stagger is still recorded on the
                // event and in the CSV, so a stagger-dependent rule can be evaluated
                // offline against a recording without re-instrumenting the game.)
                float weight = ErrorWeight(ev);

                _errorRate.RegisterEvent(now, errorRateTauSeconds, weight);
                TotalErrorsCounted++;              // raw failed-note count, unweighted
                TotalErrorWeight += weight;        // what the estimator actually saw
            }

            // ---- per-lane force margin (regulated signal for loops 2–5) ----
            // Gated by outcome class, mirroring CountsAsError() for loop 1.
            //   Missed notes are already excluded UPSTREAM, implicitly: no press was
            //     matched, so NoteResolver leaves forceMargin as NaN and the NaN check
            //     drops them. No force was applied, so there is nothing to measure.
            //   EarlyPress / LatePress / WrongLane DO carry a matched press and therefore
            //     real, non-NaN force data — they must be excluded EXPLICITLY or a timing
            //     failure leaks into the force channel.
            //   correctLane is a second, independent guard: on WrongLane the fused press
            //     belongs to another lane, and filing it under ev.lane would corrupt the
            //     wrong finger's estimator. Belt and braces — it also covers any future
            //     outcome that fuses a cross-lane press.
            if (ev.lane >= 0 && ev.lane < LaneCount &&
                !float.IsNaN(ev.forceMargin) &&
                ev.correctLane &&
                CountsAsForceSample(cls))
            {
                _forceMargin[ev.lane].AddSample(ev.forceMargin, now, forceMarginTauSeconds);
            }

            // ---- timing margin M_t (DIAGNOSTIC ONLY — deliberately not regulated) ----
            if (!float.IsNaN(ev.timingError) && ev.startWindowDuration > 0f)
                _timingMargin.AddSample(ev.startWindowDuration - ev.timingError, now,
                                        timingMarginTauSeconds);
        }

        /// <summary>
        /// How much of an error one failed note is worth: 1/chordSize for a chord
        /// member, 1 for a standalone note. Defensive against chordSize arriving as
        /// 0 or negative — an old prefab whose NoteInfo predates the chord fields,
        /// or a note spawned by something other than NoteSpawner — in which case it
        /// falls back to a full unit error, i.e. the old behaviour.
        /// </summary>
        float ErrorWeight(NoteOutcomeEvent ev)
        {
            if (!fractionalChordErrors) return 1f;
            int n = ev.chordSize;
            return (n > 1) ? 1f / n : 1f;
        }

        bool CountsAsError(OutcomeClass c)
        {
            switch (c)
            {
                case OutcomeClass.Timing:    return countTimingErrors;
                case OutcomeClass.WrongLane: return countWrongLaneErrors;
                case OutcomeClass.Force:     return countForceErrors;
                default:                     return false;   // Success / Other
            }
        }

        /// <summary>
        /// Which outcome classes are allowed to update the per-lane force-margin
        /// estimator. The force loops' analogue of CountsAsError() — see the field
        /// tooltips for why each default is what it is.
        /// </summary>
        bool CountsAsForceSample(OutcomeClass c)
        {
            switch (c)
            {
                case OutcomeClass.Success:   return forceFromHits;
                case OutcomeClass.Force:     return forceFromForceFailures;
                case OutcomeClass.Timing:    return forceFromTimingFailures;
                case OutcomeClass.WrongLane: return forceFromWrongLane;
                default:                     return false;   // Other — never counted
            }
        }

        static OutcomeClass Classify(NoteOutcome o)
        {
            if (_classification != null && _classification.TryGetValue(o, out var c)) return c;
            return OutcomeClass.Other;
        }

        /// <summary>
        /// Resolve the game-side NoteOutcome enum by NAME rather than by member
        /// reference, so adding or renaming an outcome cannot break compilation here.
        /// Anything unrecognised falls into Other and is simply not counted.
        /// </summary>
        static void BuildClassification()
        {
            if (_classification != null) return;
            _classification = new Dictionary<NoteOutcome, OutcomeClass>();

            foreach (NoteOutcome o in Enum.GetValues(typeof(NoteOutcome)))
            {
                string n = o.ToString();
                OutcomeClass c;

                if (n.Equals("Hit", StringComparison.OrdinalIgnoreCase))
                    c = OutcomeClass.Success;
                else if (n.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("UnderHeld", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("Released", StringComparison.OrdinalIgnoreCase) >= 0)
                    c = OutcomeClass.Force;
                else if (n.IndexOf("WrongLane", StringComparison.OrdinalIgnoreCase) >= 0)
                    c = OutcomeClass.WrongLane;
                else if (n.IndexOf("Miss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("Early", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("Late", StringComparison.OrdinalIgnoreCase) >= 0)
                    c = OutcomeClass.Timing;
                else
                    c = OutcomeClass.Other;

                _classification[o] = c;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  CONTROL
        // ══════════════════════════════════════════════════════════════════

        void Tick(float dt, float now)
        {
            // Keep the saturation limits live so they can be dragged in the inspector
            // while the game runs, without restarting the loop.
            reflexLoop.uMin = mapping.minDifficulty;
            reflexLoop.uMax = mapping.maxDifficulty;
            reflexLoop.plantSign = +1f;

            // Let the estimator time constant be dragged live during tuning. Note this
            // changes the PLANT, not just the filter — the gains were derived for a
            // particular tau, so re-check the response after changing it.
            _errorRate.Tau = Mathf.Max(1e-3f, errorRateTauSeconds);

            bool warming = (now - _activatedAt) < warmupSeconds;

            // ---------- Loop 1: reflex / timing ----------
            float y = _errorRate.PerMinute(now);
            if (warming)
            {
                // Observe only. Track the current output so the handover is bumpless.
                reflexLoop.Reset(_d, targetErrorsPerMinute, y);
            }
            else
            {
                _d = reflexLoop.Step(targetErrorsPerMinute, y, dt);
            }

            // ---------- Loops 2–5: force, one per lane ----------
            if (enableForceLoops)
            {
                for (int l = 0; l < LaneCount; l++)
                {
                    var loop = _forceLoops[l];
                    loop.kp = forceLoopTemplate.kp;
                    loop.ti = forceLoopTemplate.ti;
                    loop.uMin = minTau;
                    loop.uMax = maxTau;
                    loop.plantSign = -1f;              // τ↑ ⇒ margin↓, slope −1

                    // NOTE (staleness hold removed): a lane whose sample stops
                    // arriving (finger abandoned mid-session) is no longer held —
                    // it keeps regulating against whatever ema.Value last reported,
                    // which will freeze once samples stop. If the error at the
                    // moment samples stopped was non-zero, the integrator will walk
                    // that lane's τ_ℓ toward its rail with no further evidence, and
                    // nothing re-anchors it if the player returns to that lane later.
                    var ema = _forceMargin[l];

                    if (warming || !ema.HasValue)
                    {
                        // Hold until this lane has produced at least one real
                        // sample — regulating on the seeded floor/NaN would be
                        // regulating on no evidence at all, not stale evidence.
                        if (ema.HasValue) loop.Reset(_tau[l], targetForceMargin, ema.Value);
                        else              loop.ResetOutputOnly(_tau[l]);
                    }
                    else
                    {
                        _tau[l] = loop.Step(targetForceMargin, ema.Value, dt);
                    }
                }
            }

            Apply();

            if (verboseLogging)
                Debug.Log($"[PI-DDA] t={now - _activatedAt:0.0}s  y={y:0.0}/min  r={targetErrorsPerMinute:0.0}  " +
                          $"e={reflexLoop.Error:0.00}  P={reflexLoop.Proportional:0.0000}  " +
                          $"I={reflexLoop.Integral:0.0000}  d={_d:0.000}  v={NoteSpeed:0}  T={SpawnInterval:0.00}s" +
                          (warming ? "  [WARMUP]" : ""));

            OnControlTick?.Invoke();
        }

        /// <summary>
        /// The control seam. This is the ONLY method that writes GameDifficulty —
        /// the same seam RuleBasedDDAController uses, so the "delete the DDA folder
        /// and the game still runs on inspector defaults" invariant holds.
        /// </summary>
        void Apply()
        {
            if (!DifficultyAuthority.HasAuthority(this)) return;

            var gd = GameDifficulty.Instance;
            if (gd == null) return;

            _d = mapping.ClampDifficulty(_d);

            // Derived, never stored — the tuned ratio and the "interval saturates,
            // speed keeps moving" behaviour both live inside the mapping.
            gd.noteSpeed     = mapping.NoteSpeed(_d);
            gd.spawnInterval = mapping.SpawnInterval(_d);

            if (enableForceLoops && gd.requiredForce != null)
            {
                int n = Mathf.Min(LaneCount, gd.requiredForce.Length);
                for (int l = 0; l < n; l++)
                    gd.requiredForce[l] = Mathf.Clamp(_tau[l], minTau, maxTau);
            }

            if (writeGeneralDifficulty) gd.generalDifficulty = mapping.Normalised(_d);
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════════

        void ResetInternalState()
        {
            // Difficulty is a step count in the same units the rule-based controller
            // uses, so the starting point is authored directly on that scale.
            _d = mapping.ClampDifficulty(initialDifficulty);

            for (int l = 0; l < LaneCount; l++)
            {
                _tau[l] = Mathf.Clamp(initialTau, minTau, maxTau);
                _forceLoops[l].ResetOutputOnly(_tau[l]);

                // Force-margin estimator: clear any stale carry-over from a previous
                // activation (it was NOT being reset here before), then seed the
                // REPORTED value at the margin floor so recordings/plots begin at a
                // defined minimum instead of NaN. The floor is 0 - maxTau: the most
                // negative margin possible on the normalised [0,1] force scale, i.e.
                // zero applied force against the hardest requirement (maxTau) -- so it
                // "depends on what is set as the clamp" (maxTau). SeedReadout does NOT
                // mark the estimator as holding data, so the loop still HOLDS on this
                // seed until the first real force sample, which overwrites it exactly.
                _forceMargin[l].Reset();
                _forceMargin[l].SeedReadout(0f - maxTau);
            }

            // Reflex rate estimator ALWAYS starts at 0 err/min (its floor). It reads 0
            // until the first real error event, then jumps -- no seeded decay from the
            // setpoint. (During warmup the reflex loop only observes, so a 0 seed does
            // not cause a difficulty spike before real data arrives.)
            _errorRate.Reset(0f, Time.time, errorRateTauSeconds);
            reflexLoop.ResetOutputOnly(_d);

            TotalErrorsCounted = 0;
            TotalErrorWeight   = 0f;
            TotalNotesSeen     = 0;
            _seenNotes.Clear();
        }

        PIRegulator CloneForceTemplate() => new PIRegulator
        {
            kp = forceLoopTemplate.kp,
            ti = forceLoopTemplate.ti,
            uMin = minTau,
            uMax = maxTau,
            plantSign = -1f
        };

        /// <summary>Re-seed the loops from the inspector values without leaving control.</summary>
        public void ResetLoops()
        {
            ResetInternalState();
            _activatedAt  = Time.time;
            _lastTickTime = Time.time;
            _tickAccumulator = 0f;
            Apply();
        }

        void OnValidate()
        {
            if (maxTau < minTau) maxTau = minTau;
            if (controlPeriod < 0.05f) controlPeriod = 0.05f;
            if (errorRateTauSeconds < 0.5f) errorRateTauSeconds = 0.5f;
            if (forceMarginTauSeconds < 0.5f) forceMarginTauSeconds = 0.5f;
            if (timingMarginTauSeconds < 0.5f) timingMarginTauSeconds = 0.5f;

            mapping.Validate();
            initialDifficulty = mapping.ClampDifficulty(initialDifficulty);
        }

        // ══════════════════════════════════════════════════════════════════
        //  ESTIMATORS
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Unbiased exponential rate estimator for a point process. See the class
        /// summary for the derivation: continuous decay between events, a 1/τ kick
        /// on each event, giving a unit-area exponential kernel per event.
        /// </summary>
        class PointProcessRateEstimator
        {
            float _ratePerSecond;
            float _lastUpdate;

            /// <summary>
            /// Kept here (rather than passed on every read) so that a read from the
            /// HUD, from a property, or from the control tick all decay identically.
            /// The controller refreshes it whenever the inspector value changes.
            /// </summary>
            public float Tau = 10f;

            public void Reset(float ratePerSecond, float now, float tau)
            {
                _ratePerSecond = Mathf.Max(0f, ratePerSecond);
                _lastUpdate    = now;
                Tau            = Mathf.Max(1e-3f, tau);
            }

            void Decay(float now)
            {
                float dt = now - _lastUpdate;
                if (dt <= 0f || Tau <= 0f) return;
                _ratePerSecond *= Mathf.Exp(-dt / Tau);
                _lastUpdate = now;
            }

            /// <summary>
            /// Register an event of the given WEIGHT. weight = 1 is the classic
            /// unit event; a fractional weight contributes a proportionally smaller
            /// kernel, so the estimate tracks the weighted event rate Σw/Δt rather
            /// than the raw count rate. The kernel stays exponential with the same
            /// τ — only its area changes — so the estimator remains the same
            /// first-order lag the loop was designed around.
            /// </summary>
            public void RegisterEvent(float now, float tau, float weight = 1f)
            {
                if (weight <= 0f) return;
                Tau = Mathf.Max(1e-3f, tau);
                Decay(now);
                _ratePerSecond += weight / Tau;
            }

            /// <summary>
            /// Decays forward to 'now' and returns the estimate in events per MINUTE.
            /// Decaying on read means the estimate is correct at any query time
            /// without needing a per-frame update, and it keeps falling during long
            /// error-free stretches instead of freezing at the last event's value.
            /// </summary>
            public float PerMinute(float now)
            {
                Decay(now);
                return _ratePerSecond * 60f;
            }
        }

        /// <summary>
        /// EMA over irregularly-spaced samples. α = 1 − exp(−Δt/τ) makes the
        /// smoothing represent a fixed span of WALL-CLOCK time regardless of how
        /// often samples happen to arrive — unlike a fixed-α, sample-indexed EMA
        /// whose effective window drifts with the event rate.
        /// </summary>
        class TimeBasedEma
        {
            public float Value          { get; private set; } = float.NaN;
            public bool  HasValue       { get; private set; }
            public float LastSampleTime { get; private set; }

            public void Reset()
            {
                Value = float.NaN; HasValue = false; LastSampleTime = 0f;
            }

            /// <summary>
            /// Set the value REPORTED (via Value) before any real sample has arrived,
            /// WITHOUT marking the estimator as holding data. Lets a consumer read a
            /// defined floor at session start (clean recordings / plots) while the
            /// control loop still treats the lane as "no data yet" and HOLDS. The
            /// first real AddSample overwrites this exactly -- because HasValue is
            /// still false, that first sample sets Value = x directly rather than
            /// blending -- so the seed never contaminates the estimate.
            /// </summary>
            public void SeedReadout(float x)
            {
                Value = x;   // HasValue stays false on purpose.
            }

            public void AddSample(float x, float now, float tau)
            {
                if (float.IsNaN(x)) return;

                if (!HasValue)
                {
                    Value = x; HasValue = true; LastSampleTime = now;
                    return;
                }

                float dt = Mathf.Max(0f, now - LastSampleTime);
                float a  = (tau > 1e-6f) ? 1f - Mathf.Exp(-dt / tau) : 1f;
                Value   += a * (x - Value);
                LastSampleTime = now;
            }
        }
    }
}
