using UnityEngine;

/// <summary>
/// Game-side data types shared across game scripts (and referenced by DDA).
/// These live in the global namespace so they remain available even if the
/// DDA folder is deleted.
/// </summary>

public enum NoteType
{
    Tap,
    Hold,
    Strength
}

public enum NoteOutcome
{
    Hit,                // Pressed in valid timing with enough force / coverage
    Missed,             // Window closed with no associated press
    WrongLane,          // A press in a DIFFERENT lane overlapped this note's window
    UnderHeld,          // Hold/Strength: press did not cover enough of the window
    ForceInsufficient,  // Force did not meet the note's requirement
    EarlyPress,         // Press began before the note entered the pickup
    LatePress           // Press began after the note left the pickup (within grace)
}

/// <summary>
/// What the game determined happened to a note when its pickup window resolved.
/// Carries everything the GAME knows: timing window, configured thresholds, and
/// what the game's own input polling observed. NOT enriched with DDA-side input
/// statistics — that fusion happens in NoteResolver.
/// </summary>
public class NoteResolutionEvent
{
    // Identification
    public int noteId;                  // GameObject.GetInstanceID()
    public NoteType type;
    public int lane;

    // Outcome (game's authoritative call)
    public NoteOutcome outcome;

    // Window (observed from pickup collisions)
    public float tEnter;
    public float tExit;
    public float windowDuration;        // tExit − tEnter: full traversal for all note types.
                                        // For holds this is the complete hold length + pickup.
                                        // Use startWindowDuration for timing-margin M_t.

    /// <summary>
    /// Tap-equivalent press window: (tapNoteLength + pickupLength) / noteSpeed.
    /// Equals windowDuration for tap notes. For hold and strength notes this is
    /// the window within which the player MUST begin pressing — the same timing
    /// challenge as a tap. Used to compute M_t = startWindowDuration − timingError.
    /// </summary>
    public float startWindowDuration;

    // What the note required (snapshot of NoteInfo at resolution time)
    public float requiredForce;
    public float coverageThreshold;

    // Chord identity (snapshot of NoteInfo at resolution time; see NoteInfo.cs).
    // Baked at spawn by NoteSpawner and carried here so the DDA can tell how many
    // notes the player was asked to play AT ONCE when this one resolved — a note
    // failed as one of four is not the same evidence as a note failed alone.
    // PIDifficultyController weights a failed note as 1/chordSize of an error.
    public int chordId = -1;             // shared across a chord; -1 if standalone
    public int chordSize = 1;            // 1 if standalone
    public int chordOnsetIndex;          // arrival order within the chord; 0 = on-beat member
    public int chordStaggerEighths;      // inter-onset gap in eighths of a beat; 0 = synchronous

    // What the game observed via InputManagerScript polling
    public float observedMaxForce;      // peak force on this lane during the window
    public float observedAvgForce;      // mean force across samples in the window
    public float observedCoverage;      // [0,1] fraction of window held above requiredForce

    // Press timing the game noticed (NaN if not observed)
    public float correctLanePressedAt;  // tPress for first press in note's lane, NaN if none
    public int wrongLanePressed;        // first OTHER lane pressed during window, -1 if none
    public float wrongLanePressedAt;    // when the wrong-lane press started, NaN if none

    // For the visual feedback layer (NoteFeedback). DDA ignores this field.
    public GameObject noteObj;
}

/// <summary>
/// Per-frame snapshot of a note's live state, emitted by NoteStatesBroadcaster
/// while a note is inside the pickup zone.
///
/// Two kinds of events:
///   IN-PROGRESS  (succeeded=false, failed=false): emitted every frame while
///                the note is actively being played. Carries live force and
///                progress for force-meter and hold-progress visuals.
///
///   CONCLUSIVE   (succeeded=true XOR failed=true): emitted exactly once per
///                note when the outcome is known instantly — no grace period.
///                NoteFeedback colors the note; ScoreManager updates the score.
///
/// The DDA does NOT subscribe to these events. It uses OnNoteResolved instead.
/// </summary>
public class NoteStateEvent
{
    public int noteId;
    public GameObject noteObj;   // null-check before use

    public int lane;
    public NoteType type;

    // Live data (valid on every event, in-progress or conclusive)
    public float currentForce;   // force on this lane right now, [0,1]
    public float holdProgress;   // heldTime / expectedWindowDuration, [0,1]

    // Conclusion flags — at most one is true; both false = in-progress
    public bool succeeded;
    public bool failed;
}
