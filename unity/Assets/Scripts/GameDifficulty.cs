using UnityEngine;

/// <summary>
/// Central store of gameplay difficulty parameters. Game scripts read from
/// here (NoteSpawner, NoteMover, HoldNoteScript, etc.); the DDA OutputMapper
/// writes here when present.
///
/// THE INVARIANT:
///   - If GameDifficulty.Instance is set in the scene → game uses these values.
///   - If the DDA folder is deleted and nothing writes here, the Inspector
///     values become the static game configuration.
///   - If GameDifficulty.Instance is somehow null at runtime, individual game
///     scripts fall back to their own local default values.
///
/// SCHEMA NOTE — per-lane force thresholds (June 2026):
/// The control vector u contains τ_ℓ, ℓ ∈ {1..4} — a separate force threshold
/// per lane. requiredForce is therefore an array of length laneCount, NOT a
/// scalar. NoteSpawner must index by the spawned note's lane:
///     noteInfo.requiredForce = GameDifficulty.Instance.requiredForce[lane];
/// The OutputMapper writes the full array (or individual entries) per control tick.
/// </summary>
public class GameDifficulty : MonoBehaviour
{
    public static GameDifficulty Instance { get; private set; }

    [Header("Reflexes channel")]
    [Tooltip("Horizontal speed of notes (units/second). Read by NoteMover and " +
             "by NoteSpawner (needed to convert hold durations and stagger " +
             "offsets into world distances).")]
    public float noteSpeed = 500f;
    [Tooltip("Seconds between beats. This IS the beat period: f_s = 1/spawnInterval. " +
             "Notes only spawn on beats. Read by NoteSpawner.")]
    public float spawnInterval = 1f;

    [Header("Coordination channel")]
    [Tooltip("Probability a spawn becomes a chord (vs a single note). " +
             "By design this is FIXED for a session — the OutputMapper should " +
             "not drive it. Read by NoteSpawner.")]
    [Range(0f, 1f)] public float simultaneousChance = 0.25f;

    [Tooltip("n_c — MAX TOTAL notes per chord (== control action notesPerChord). " +
             "When a chord fires, its size is random in [2, maxSimultaneousLanes]. " +
             "NOTE: semantics changed — this is now the TOTAL note count, not the " +
             "number of EXTRA lanes. Read by NoteSpawner.")]
    public int maxSimultaneousLanes = 2;

    [Tooltip("Chord mismatch — staggers chord onsets by this many EIGHTHS of a beat " +
             "between consecutive notes (0 = synchronous, 8 = a full beat between " +
             "onsets). Control action #4. All notes in a chord still END together, " +
             "so staggered notes simply get shorter. Read by NoteSpawner.")]
    [Range(0, 8)] public int chordMismatch = 0;

    [Tooltip("Probability a spawn is a Hold note (vs Tap). Read by NoteSpawner.")]
    [Range(0f, 1f)] public float holdNoteChance = 0.5f;

    [Header("Strength channel — per-lane force thresholds (τ_ℓ)")]
    [Tooltip("Required peak (or average, for holds) force per lane, normalized [0,1]. " +
             "One entry per lane; index 0..laneCount-1. NoteSpawner reads " +
             "requiredForce[lane] and bakes it into NoteInfo at spawn time. " +
             "Written by the DDA OutputMapper (control vector τ_1..τ_4). " +
             "Length MUST be 4 for the current 4-lane game.")]
    [Range(0f, 1f)] public float[] requiredForce = new float[] { 0.4f, 0.4f, 0.4f, 0.4f };

    [Tooltip("How many seconds before the hold note's trailing edge physically exits " +
             "the pickup zone the player is allowed to release. A hold is considered " +
             "successful once it has been held for (holdDuration − holdEndMargin) seconds. " +
             "Converted to a per-note coverageThreshold at spawn time by NoteSpawner. " +
             "NOT in the control vector — design constant for this iteration.")]
    public float holdEndMargin = 0.1f;

    [Tooltip("Minimum hold-note duration, in BEATS. Holds are quantised to " +
             "half-beats, so the effective minimum is rounded up to the nearest " +
             "0.5. (Replaces the old holdLengthMin scale-multiplier.) Read by NoteSpawner.")]
    public float holdBeatsMin = 0.5f;
    [Tooltip("Maximum hold-note duration, in BEATS. A hold's end always lands on a " +
             "beat or half-beat. (Replaces the old holdLengthMax scale-multiplier.) " +
             "Read by NoteSpawner.")]
    public float holdBeatsMax = 3f;

    [Header("General / aggregate signal")]
    [Tooltip("Aggregate scalar used by purely cosmetic systems (e.g. background color). " +
             "Not directly tied to gameplay; controllers may set it for visualization.")]
    [Range(1f, 2f)] public float generalDifficulty = 1f;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // Defensive: ensure requiredForce has the right length even if the
        // inspector array was resized incorrectly.
        if (requiredForce == null || requiredForce.Length < 4)
        {
            var f = new float[4];
            float fill = (requiredForce != null && requiredForce.Length > 0) ? requiredForce[0] : 0.4f;
            for (int i = 0; i < 4; i++)
                f[i] = (requiredForce != null && i < requiredForce.Length) ? requiredForce[i] : fill;
            requiredForce = f;
        }
    }

    /// <summary>
    /// Safe accessor — returns requiredForce[lane], or a fallback if the array
    /// is malformed. Game scripts should prefer this over indexing directly.
    /// </summary>
    public float GetRequiredForce(int lane)
    {
        if (requiredForce != null && lane >= 0 && lane < requiredForce.Length)
            return requiredForce[lane];
        return 0.4f;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
