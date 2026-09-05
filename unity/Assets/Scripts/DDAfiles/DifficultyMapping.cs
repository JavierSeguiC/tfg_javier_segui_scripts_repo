using UnityEngine;

namespace DDA
{
    /// <summary>
    /// THE d → (noteSpeed, spawnInterval) MAPPING, shared by every DDA controller.
    ///
    /// This is a plain [Serializable] class, NOT a ScriptableObject: each controller
    /// owns its own instance and therefore its own inspector values, so the PI
    /// controller and the rule-based controller can be tuned independently — while
    /// the mapping MATHS lives in exactly one place and cannot drift between them.
    ///
    /// ── DIFFICULTY IS A STEP COUNT, NOT A FRACTION ──────────────────────────
    /// d is no longer a normalised [0,1] knob. One unit of d is ONE SUCCESS worth
    /// of movement, which makes the scale physically meaningful and identical in
    /// both controllers: the rule-based controller adds 1.0 per cleared obstacle,
    /// and the PI controller's output is denominated in the same units.
    ///
    /// ── NOTE SPEED: LINEAR EVERYWHERE ───────────────────────────────────────
    ///     v(d) = speedStep · d
    /// No kinks, no clamp of its own. Speed is always free to keep moving — that
    /// is the whole point of the design below.
    ///
    /// ── SPAWN INTERVAL: FLAT → LINEAR → FLAT ────────────────────────────────
    ///     T(d) = clamp( refInterval − intervalStep·(d − dRef),
    ///                   minSpawnInterval, maxSpawnInterval )
    /// The clamp IS the piecewise behaviour — no branching needed. Outside the two
    /// breakpoints the interval saturates and only v keeps changing; between them
    /// the two actuators move together, locked at the hand-tuned ratio
    ///     speedStep / intervalStep   (5 / 0.016667 = 300 wu/s per second of interval)
    /// which is the same constant implied by the rule-based fail steps
    /// (15 / 0.05 = 300), i.e. the ratio is exact and direction-independent.
    ///
    /// ── THE BREAKPOINTS ARE DERIVED, NOT AUTHORED ───────────────────────────
    /// Everything follows from one calibration anchor — the tuned operating point
    /// (refSpeed, refInterval) = (300 wu/s, 1.2 s):
    ///     dRef = refSpeed / speedStep                                    = 60
    ///     dLo  = dRef − (maxSpawnInterval − refInterval)/intervalStep    = 12
    ///     dHi  = dRef + (refInterval − minSpawnInterval)/intervalStep    = 123
    /// giving v(dLo) = 60 wu/s and v(dHi) = 615 wu/s. Below d=12 only speed moves
    /// (interval pinned at 2.0 s); above d=123 only speed moves (interval pinned at
    /// 0.15 s); in between both move together. Change any authored number and the
    /// breakpoints re-derive themselves — they can never desync from the ratio.
    ///
    /// ── CLAMPS ──────────────────────────────────────────────────────────────
    /// d itself is clamped to [minDifficulty, maxDifficulty] so a long streak (or a
    /// wound-up integrator) cannot run the speed away to infinity.
    /// </summary>
    [System.Serializable]
    public class DifficultyMapping
    {
        [Header("Step sizes (one unit of d = one success)")]
        [Tooltip("Note speed added per unit of difficulty, wu/s. v(d) = speedStep · d.")]
        public float speedStep = 5f;

        [Tooltip("Spawn interval REMOVED per unit of difficulty, seconds. Together with " +
                 "speedStep this fixes the hand-tuned feel ratio " +
                 "speedStep/intervalStep = 5/0.016667 = 300 wu/s per second of interval.")]
        public float intervalStep = 1f / 60f;   // 0.0166667 s

        [Header("Spawn interval clamps")]
        [Tooltip("Fastest (hardest) spawn interval. Once reached, ONLY note speed keeps rising.")]
        public float minSpawnInterval = 0.15f;
        [Tooltip("Slowest (easiest) spawn interval. Once reached, ONLY note speed keeps falling.")]
        public float maxSpawnInterval = 2.0f;

        [Header("Calibration anchor (the tuned operating point)")]
        [Tooltip("Note speed at the anchor. Defines dRef = refSpeed/speedStep, and with it " +
                 "both interval breakpoints. Default 300 wu/s ⇒ dRef = 60.")]
        public float refSpeed = 300f;
        [Tooltip("Spawn interval at the anchor, seconds. Default 1.2 s, i.e. the mapping " +
                 "passes exactly through the hand-tuned (300 wu/s, 1.2 s) point.")]
        public float refInterval = 1.2f;

        [Header("Difficulty clamps")]
        [Tooltip("Floor on d. Design intent: still trivially clearable by anyone. " +
                 "At the defaults d=5 ⇒ 25 wu/s at 2.0 s.")]
        public float minDifficulty = 5f;
        [Tooltip("Ceiling on d. Design intent: unplayable for anyone, and a guard against " +
                 "a runaway streak or a wound-up integrator. At the defaults d=200 ⇒ " +
                 "1000 wu/s at 0.15 s.")]
        public float maxDifficulty = 200f;

        // ── derived quantities ────────────────────────────────────────────────

        /// <summary>Hand-tuned feel ratio, wu/s per second of spawn interval.</summary>
        public float SpeedToIntervalRatio =>
            intervalStep > 1e-9f ? speedStep / intervalStep : 0f;

        /// <summary>Difficulty at the calibration anchor. Defaults to 60.</summary>
        public float DRef => speedStep > 1e-9f ? refSpeed / speedStep : 0f;

        /// <summary>
        /// Lower breakpoint: below this, spawn interval is pinned at maxSpawnInterval
        /// and only note speed moves. Defaults to 12 (⇒ 60 wu/s).
        /// </summary>
        public float DLo => intervalStep > 1e-9f
            ? DRef - (maxSpawnInterval - refInterval) / intervalStep
            : DRef;

        /// <summary>
        /// Upper breakpoint: above this, spawn interval is pinned at minSpawnInterval
        /// and only note speed moves. Defaults to 123 (⇒ 615 wu/s).
        /// </summary>
        public float DHi => intervalStep > 1e-9f
            ? DRef + (refInterval - minSpawnInterval) / intervalStep
            : DRef;

        public float NoteSpeedAtDLo => NoteSpeed(DLo);
        public float NoteSpeedAtDHi => NoteSpeed(DHi);

        // ── the mapping itself ────────────────────────────────────────────────

        /// <summary>Note speed for a difficulty level. Linear, unkinked, unclamped.</summary>
        public float NoteSpeed(float d) => speedStep * d;

        /// <summary>
        /// Spawn interval for a difficulty level. The clamp produces the
        /// flat → linear → flat shape without any explicit branching.
        /// </summary>
        public float SpawnInterval(float d) => Mathf.Clamp(
            refInterval - intervalStep * (d - DRef),
            minSpawnInterval, maxSpawnInterval);

        /// <summary>Spawn frequency f_s = 1/T, Hz.</summary>
        public float SpawnFrequency(float d)
        {
            float t = SpawnInterval(d);
            return t > 1e-4f ? 1f / t : 0f;
        }

        /// <summary>Clamp a difficulty level to the configured working range.</summary>
        public float ClampDifficulty(float d) => Mathf.Clamp(d, minDifficulty, maxDifficulty);

        /// <summary>
        /// Inverse of NoteSpeed — useful for authoring a starting point as an
        /// absolute speed rather than as a step count.
        /// </summary>
        public float DifficultyForNoteSpeed(float v) =>
            speedStep > 1e-9f ? v / speedStep : 0f;

        /// <summary>d mapped to [0,1] across the clamp range, for plots and cosmetic hooks.</summary>
        public float Normalised(float d) => Mathf.InverseLerp(minDifficulty, maxDifficulty, d);

        /// <summary>True when the spawn interval is saturated and only speed is moving.</summary>
        public bool IntervalSaturated(float d) => d <= DLo || d >= DHi;

        /// <summary>Keep inspector edits self-consistent. Call from OnValidate.</summary>
        public void Validate()
        {
            if (speedStep < 1e-4f) speedStep = 1e-4f;
            if (intervalStep < 1e-6f) intervalStep = 1e-6f;
            if (minSpawnInterval < 0.01f) minSpawnInterval = 0.01f;
            if (maxSpawnInterval < minSpawnInterval) maxSpawnInterval = minSpawnInterval;
            refInterval = Mathf.Clamp(refInterval, minSpawnInterval, maxSpawnInterval);
            if (refSpeed < 0f) refSpeed = 0f;
            if (minDifficulty < 0f) minDifficulty = 0f;
            if (maxDifficulty < minDifficulty) maxDifficulty = minDifficulty;
        }

        /// <summary>One-line summary for HUDs and logs.</summary>
        public string Describe() =>
            $"d∈[{minDifficulty:0},{maxDifficulty:0}]  " +
            $"breakpoints d={DLo:0.#}({NoteSpeedAtDLo:0} wu/s) … d={DHi:0.#}({NoteSpeedAtDHi:0} wu/s)  " +
            $"T∈[{minSpawnInterval:0.00},{maxSpawnInterval:0.00}]s  ratio {SpeedToIntervalRatio:0}";
    }
}
