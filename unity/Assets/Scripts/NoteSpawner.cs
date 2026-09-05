using UnityEngine;

/// <summary>
/// Rhythmic note spawner.
///
/// TIMING MODEL
///   spawnInterval IS the beat period (f_s = 1/spawnInterval). Notes only ever
///   spawn on beats. Every note's LEADING EDGE is placed exactly on the beat
///   grid so the player can time the hit by following the beat.
///
///   Because notes are stretched about their CENTRE, the leading edge sits a
///   half-width ahead of the spawn point. We compensate by pushing the centre
///   back by one half-width (along the travel axis) so the leading edge lands
///   on the spawn point. Notes travel LEFT (-x), so the leading edge is the
///   left/min-x edge → centre = leadingEdge + halfWidth. (Flip notesTravelLeft
///   if NoteMover ever moves notes the other way.)
///
/// HOLD LENGTH / ENDS
///   A hold's on-screen length is chosen so its on/off window lasts a whole
///   number of HALF-BEATS, from holdBeatsMin up to holdBeatsMax. Length in world
///   units = durationSeconds * noteSpeed, so the trailing edge crosses any fixed
///   point exactly durationSeconds after the leading edge → the hold "ends" on a
///   beat or half-beat. Sizing is delegated to HoldNoteScript.SetWindowDuration.
///
/// SPACING (no overlap in a lane)
///   After a note/chord, the next spawn waits until at least a half-beat after
///   the previous one ENDED, rounded up to the next beat (we can only spawn on
///   beats). For taps this is automatically one beat; for a 3.5-beat hold the
///   next spawn is the 4th beat, etc.
///
/// CHORDS
///   simultaneousChance (fixed) decides chord vs single. n_c
///   (maxSimultaneousLanes) is the MAX size; actual size is random in [2, n_c].
///   chordMismatch staggers onsets by that many eighths of a beat per step, while
///   all chord notes still END together (later notes are simply shorter).
///
///   Every spawned note is stamped with its chord's identity on NoteInfo:
///   chordId (shared, -1 standalone), chordSize, chordOnsetIndex (arrival order
///   within the chord, 0 = on-beat) and chordStaggerEighths. This metadata
///   propagates through NoteHitDetector → NoteResolver into the DDA, where
///   PIDifficultyController weights a failed chord member as 1/chordSize of an
///   error. Spawning behaviour itself is unchanged by it.
///
/// GHOST BEAT STREAM
///   In addition to the gated musical notes, an INVISIBLE tap-sized "ghost" note is
///   spawned on every beat — or on a SUBDIVISION of the beat (half / quarter / eighth)
///   so a slow tempo still feels like a steady pulse — unaffected by the spacing gate.
///   Ghosts use the same speed and the same leading-edge-on-grid placement as real notes,
///   so every on-beat note coincides with a ghost, and the beats a hold "skips" still get
///   one. The subdivision grid is locked to the real-note beat grid, so the on-beat ghost
///   always lands with the on-beat note; extra subdivisions are metronome fill. When a
///   ghost reaches the GhostPickup its arrival IS the beat — a steady, physically-exact
///   pulse. See GhostPickup.cs. (Optional: leave ghostNotePrefab empty to disable.)
///
/// At spawn time the current force thresholds and hold-end margin are baked onto each
/// note's NoteInfo, so later GameDifficulty changes don't affect notes already
/// in flight. For hold notes, coverageThreshold is derived from the actual hold
/// duration and holdEndMargin so that the player only needs to hold for
/// (duration − holdEndMargin) seconds, regardless of note length.
/// </summary>
public class NoteSpawner : MonoBehaviour
{
    /// <summary>Ghost pulse density: the value is how many ghosts fire per beat.</summary>
    public enum BeatSubdivision { Whole = 1, Half = 2, Quarter = 4, Eighth = 8 }

    [Header("References")]
    public GameObject tapNotePrefab;
    public GameObject holdNotePrefab;
    public Transform[] spawnPoints; // One per lane, index matches lane number

    [Header("Ghost beat stream")]
    [Tooltip("Invisible, tap-sized note spawned on EVERY beat to drive a steady beat via " +
             "GhostPickup. Same dimensions as a tap note but with rendering off. Leave empty to disable.")]
    public GameObject ghostNotePrefab;
    [Tooltip("Where ghost notes spawn. Its x MUST match the lane spawn points' x so ghost arrivals " +
             "coincide with on-beat notes. If unset, falls back to spawnPoints[0] (same x, its row).")]
    public Transform ghostSpawnPoint;
    [Tooltip("Master switch for the ghost beat stream.")]
    public bool spawnGhostBeat = true;
    [Tooltip("How often ghosts fire within each beat. Denser subdivisions fill in a slow tempo so " +
             "the pulse doesn't feel too far apart. Whole = one per beat; Eighth = eight per beat.")]
    public BeatSubdivision ghostSubdivision = BeatSubdivision.Whole;

    [Header("Geometry")]
    [Tooltip("Notes move in -x (leftward). Leave true for the standard layout " +
             "(spawn right, pickup left). Flip if NoteMover moves notes the other way.")]
    public bool notesTravelLeft = true;

    [Header("Fallback values (used only if no GameDifficulty in scene)")]
    public float fallbackNoteSpeed = 5f;
    public float fallbackSpawnInterval = 1f;
    [Range(0f, 1f)] public float fallbackHoldNoteChance = 0.5f;
    [Range(0f, 1f)] public float fallbackSimultaneousChance = 0.25f;
    [Tooltip("n_c fallback — MAX TOTAL notes per chord (not 'extra' lanes).")]
    public int fallbackMaxSimultaneousLanes = 2;
    [Range(0, 8)] public int fallbackChordMismatch = 0;
    [Range(0f, 1f)] public float fallbackRequiredForce = 0.4f;
    [Tooltip("Fallback for holdEndMargin (seconds before trailing edge that a hold is considered complete).")]
    public float fallbackHoldEndMargin = 0.1f;
    public float fallbackHoldBeatsMin = 0.5f;
    public float fallbackHoldBeatsMax = 3f;

    // --- beat clock state ---
    private float beatClock;
    private int   currentBeat   = -1; // index of the last beat that elapsed
    private int   nextSpawnBeat = 0;  // earliest beat index we're allowed to spawn on
    private long  lastGhostIndex = -1; // last ghost subdivision index we emitted (monotonic)

    // --- chord id state ---
    private int   nextChordId    = 0;  // monotonic; each spawned chord gets a fresh id

    // --- quantisation constants (in beats) ---
    private const float HoldQuantum    = 0.125f; // holds end on eighth-beats
    private const float MinTailBeats   = 0.25f; // shortest hold for the most-staggered chord note
    private const float Epsilon        = 1e-4f;

    void Update()
    {
        float interval = GameDifficulty.Instance != null
            ? GameDifficulty.Instance.spawnInterval
            : fallbackSpawnInterval;
        if (interval <= 0f) interval = fallbackSpawnInterval; // guard against /0

        // Advance the beat grid for the time that elapsed this frame.
        beatClock += Time.deltaTime;
        while (beatClock >= interval)
        {
            beatClock -= interval;
            currentBeat++;
        }

        // Ghost beat: invisible notes on a subdivision of the beat, UNGATED by spacing.
        // The subdivision grid is locked to the SAME beat grid as the real notes (it is
        // built from currentBeat + beatClock, not a free-running clock), so the on-beat
        // subdivision fires on the frame currentBeat advances and therefore coincides with
        // any on-beat real note (both spawn at the leading edge that frame). In-between
        // subdivisions are pure metronome fill. At most one ghost per frame (no bunching);
        // very dense subdivisions at a fast tempo may drop sub-ticks, which is fine since
        // this feature targets SLOW tempi where the beats feel too far apart.
        if (spawnGhostBeat && ghostNotePrefab != null && currentBeat >= 0)
        {
            int   sub         = Mathf.Max(1, (int)ghostSubdivision);   // ghosts per beat
            float ghostPeriod = interval / sub;                        // seconds between ghosts
            int   subElapsed  = Mathf.Clamp(
                                    Mathf.FloorToInt((beatClock + Epsilon) / ghostPeriod), 0, sub - 1);
            long  ghostIndex  = (long)currentBeat * sub + subElapsed;  // monotonic across beats

            if (ghostIndex > lastGhostIndex)
            {
                SpawnGhost();
                lastGhostIndex = ghostIndex;
            }
        }

        // At most one spawn per frame; the gate enforces inter-note spacing.
        if (currentBeat >= nextSpawnBeat)
        {
            float endBeats = SpawnChord(interval);
            // At least a half-beat after the chord ends, rounded up to a beat.
            int gap = Mathf.CeilToInt(endBeats + 0.5f - Epsilon);
            if (gap < 1) gap = 1;
            nextSpawnBeat = currentBeat + gap;
        }
    }

    /// <summary>
    /// Spawns one beat's worth of notes (single or chord). Returns the chord's
    /// common END time, in beats from this beat (used to schedule the next spawn).
    /// </summary>
    float SpawnChord(float beatPeriod)
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return 0f;

        var d = GameDifficulty.Instance;
        float noteSpeed  = d != null ? d.noteSpeed            : fallbackNoteSpeed;
        float simChance  = d != null ? d.simultaneousChance   : fallbackSimultaneousChance;
        int   nC         = d != null ? d.maxSimultaneousLanes : fallbackMaxSimultaneousLanes;
        int   mismatch   = d != null ? d.chordMismatch        : fallbackChordMismatch;
        float holdChance = d != null ? d.holdNoteChance       : fallbackHoldNoteChance;
        float holdEndMargin = d != null ? d.holdEndMargin    : fallbackHoldEndMargin;
        float holdMin    = d != null ? d.holdBeatsMin         : fallbackHoldBeatsMin;
        float holdMax    = d != null ? d.holdBeatsMax         : fallbackHoldBeatsMax;

        mismatch = Mathf.Clamp(mismatch, 0, 8);

        // --- chord composition ---
        bool isChord = (nC >= 2) && (Random.value < simChance);
        int noteCount = isChord ? Random.Range(2, nC + 1) : 1;   // [2, n_c] when a chord
        noteCount = Mathf.Clamp(noteCount, 1, spawnPoints.Length);

        // All notes in a chord share the same type (so they can end together).
        bool useHold = Random.value < holdChance;

        int[] lanes = PickDistinctLanes(noteCount);

        // --- onset stagger: 'mismatch' eighths of a beat between consecutive notes ---
        float staggerBeats   = mismatch / 8f;
        float maxOffsetBeats  = (noteCount - 1) * staggerBeats;   // onset of the last note

        // --- common end (in beats from this beat) ---
        float commonEndBeats = useHold
            ? ChooseHoldEndBeats(holdMin, holdMax, maxOffsetBeats)
            : maxOffsetBeats;   // taps have no length: "end" = last onset

        // --- chord metadata ---
        // Notes spawned together share one chordId so the GAME side can later tell whether the
        // WHOLE chord was cleared. Standalone notes get chordId -1. This is pure metadata baked
        // alongside the existing NoteInfo baking: it changes nothing about how many notes spawn,
        // their lanes, timing, type or force.
        //
        // The DDA now READS this metadata (it propagates NoteInfo → NoteResolutionEvent →
        // NoteOutcomeEvent), because PIDifficultyController weights a failed chord member as
        // 1/chordSize of an error instead of a full one. chordOnsetIndex and staggerEighths are
        // carried for offline analysis: they let a recording distinguish a synchronous 4-note
        // chord from four notes merely sharing an id but spread across a beat.
        int chordId     = (noteCount >= 2) ? nextChordId++ : -1;
        int chordSize   = noteCount;
        // Stagger only means something between two onsets, so a standalone note reports 0
        // rather than the authored mismatch it never experienced.
        int staggerEighths = (noteCount >= 2) ? mismatch : 0;

        // --- spawn each note ---
        for (int i = 0; i < noteCount; i++)
        {
            float onsetBeats   = i * staggerBeats;
            float onsetSeconds = onsetBeats * beatPeriod;
            // Holds: same end for everyone, so a later onset means a shorter note.
            float durationSec  = useHold ? (commonEndBeats - onsetBeats) * beatPeriod : 0f;

            float reqForce = d != null ? d.GetRequiredForce(lanes[i]) : fallbackRequiredForce;

            // i IS the onset index: lanes[] is shuffled, so i orders notes by ARRIVAL, not by
            // lane. i = 0 is the on-beat member (the one that coincides with the ghost).
            SpawnNoteInLane(lanes[i], useHold, onsetSeconds, durationSec,
                            noteSpeed, reqForce, holdEndMargin,
                            chordId, chordSize, i, staggerEighths);
        }

        return commonEndBeats;
    }

    /// <summary>
    /// Picks the common hold end (beats), quantised to half-beats within
    /// [holdMin, holdMax], guaranteeing the most-staggered note still gets a
    /// valid tail (>= MinTailBeats). If the stagger forces the end above holdMax,
    /// it is clamped up so the "all notes end together" invariant is preserved.
    /// </summary>
    float ChooseHoldEndBeats(float holdMin, float holdMax, float maxOffsetBeats)
    {
        float minEnd = Mathf.Max(holdMin, maxOffsetBeats + MinTailBeats);
        float maxEnd = Mathf.Max(holdMax, minEnd);   // never below minEnd

        int minSteps = Mathf.CeilToInt (minEnd / HoldQuantum - Epsilon);
        int maxSteps = Mathf.FloorToInt(maxEnd / HoldQuantum + Epsilon);
        if (minSteps < 1)       minSteps = 1;        // at least one half-beat
        if (maxSteps < minSteps) maxSteps = minSteps;

        int steps = Random.Range(minSteps, maxSteps + 1);   // inclusive
        return steps * HoldQuantum;
    }

    void SpawnNoteInLane(int lane, bool useHold, float onsetSeconds, float durationSeconds,
                         float noteSpeed, float reqForce, float holdEndMargin,
                         int chordId, int chordSize, int chordOnsetIndex, int chordStaggerEighths)
    {
        Transform point = spawnPoints[lane];
        GameObject prefab = useHold ? holdNotePrefab : tapNotePrefab;
        if (prefab == null) return;

        GameObject note = Instantiate(prefab, point.position, Quaternion.identity);

        // Size hold notes so their pickup window lasts exactly durationSeconds.
        if (useHold)
        {
            var hold = note.GetComponent<HoldNoteScript>();
            if (hold != null) hold.SetWindowDuration(durationSeconds, noteSpeed);
            else              SetWorldLengthDirect(note, durationSeconds * noteSpeed);
        }

        // Place the LEADING edge on the beat grid (+ stagger), compensating for
        // the centre-anchored stretch. travelDir = sign of the x-velocity.
        int   travelDir    = notesTravelLeft ? -1 : 1;
        float halfWidth    = GetWorldHalfWidth(note);
        // A later onset means the note must arrive later → start further BACK
        // along the travel direction (i.e. shifted opposite to travelDir).
        float leadingEdgeX = point.position.x - travelDir * onsetSeconds * noteSpeed;
        // Leading edge is the frontmost point in travel dir; centre is one
        // half-width behind it: centre = leadingEdge - travelDir * halfWidth.
        float centreX      = leadingEdgeX - travelDir * halfWidth;

        // Z is intentionally identical to the spawn point's Z (and therefore to the
        // pickup's Z) — draw order between notes/strings/pickups is handled entirely
        // by Sorting Layer + Order in Layer on each prefab's SpriteRenderer, NOT by
        // Z offsets. With a perspective camera, ANY Z difference between coplanar
        // game elements gets visually distorted as objects move through the angled
        // frustum, causing apparent "popping" through other elements mid-flight.
        // Do not reintroduce a Z offset here.
        note.transform.position = new Vector3(centreX, point.position.y, point.position.z);

        // Bake current difficulty into the note's NoteInfo at spawn time.
        // For hold/strength notes: convert the fixed end-margin into a fraction
        // of this note's actual window duration, so the player only needs to hold
        // for (durationSeconds − holdEndMargin) seconds regardless of note length.
        var info = note.GetComponent<NoteInfo>();
        if (info != null)
        {
            info.requiredForce = reqForce;

            // Chord grouping. Read by the game side AND (since Aug 2026) by the DDA:
            // PIDifficultyController weights a failed chord member as 1/chordSize.
            info.chordId             = chordId;
            info.chordSize           = chordSize;
            info.chordOnsetIndex     = chordOnsetIndex;
            info.chordStaggerEighths = chordStaggerEighths;

            if (useHold && durationSeconds > 0f)
            {
                float requiredSeconds = Mathf.Max(0f, durationSeconds - holdEndMargin);
                info.coverageThreshold = Mathf.Clamp01(requiredSeconds / durationSeconds);
            }
            // Tap notes: coverageThreshold is unused; leave it at the prefab default.
        }
    }

    /// <summary>
    /// Spawns one invisible ghost note with onset 0 — leading edge exactly on the beat
    /// grid, same width-compensated placement and same speed as real notes. Its arrival
    /// at the GhostPickup is the beat. No NoteInfo baking and no hold sizing: a ghost is
    /// never resolved as a real hit (keep it on a physics layer the real pickups ignore).
    /// </summary>
    void SpawnGhost()
    {
        Transform origin = ghostSpawnPoint != null
            ? ghostSpawnPoint
            : (spawnPoints != null && spawnPoints.Length > 0 ? spawnPoints[0] : null);
        if (origin == null) return;

        GameObject ghost = Instantiate(ghostNotePrefab, origin.position, Quaternion.identity);

        int   travelDir    = notesTravelLeft ? -1 : 1;
        float halfWidth    = GetWorldHalfWidth(ghost);
        float leadingEdgeX = origin.position.x;                 // onset 0 → leading edge on the grid
        float centreX      = leadingEdgeX - travelDir * halfWidth;

        ghost.transform.position = new Vector3(centreX, origin.position.y, origin.position.z);
    }

    int[] PickDistinctLanes(int count)
    {
        // Fisher-Yates partial shuffle on lane indices.
        int[] indices = new int[spawnPoints.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        for (int i = 0; i < count; i++)
        {
            int j = Random.Range(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        int[] result = new int[count];
        System.Array.Copy(indices, result, count);
        return result;
    }

    // World-space half-width of a (non-rotated) note, measured AFTER any scaling
    // so the leading-edge placement is exact regardless of prefab scale.
    static float GetWorldHalfWidth(GameObject note)
    {
        var rend = note.GetComponentInChildren<Renderer>();
        if (rend != null) return rend.bounds.size.x * 0.5f;
        var col = note.GetComponentInChildren<Collider2D>();
        if (col != null)  return col.bounds.size.x * 0.5f;
        return 0f;
    }

    // Fallback sizer if a hold prefab somehow lacks HoldNoteScript.
    static void SetWorldLengthDirect(GameObject note, float worldLength)
    {
        worldLength = Mathf.Max(0.0001f, worldLength);
        var sr = note.GetComponentInChildren<SpriteRenderer>();
        if (sr == null || sr.sprite == null) return;
        float localWidth = sr.sprite.bounds.size.x;
        if (localWidth <= 0f) return;
        Vector3 s = note.transform.localScale;
        s.x = worldLength / localWidth;
        note.transform.localScale = s;
    }
}
