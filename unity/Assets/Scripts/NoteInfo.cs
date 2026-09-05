using UnityEngine;

/// <summary>
/// Attach to each note prefab. Carries the note's type and its requirements.
/// The note does NOT need to know its lane — that is determined physically by
/// which LanePickup it passes through.
///
/// This is GAME data. The DDA reads it (when fusing outcomes with input data)
/// but never modifies it. If the DDA folder is deleted, this still works.
/// </summary>
public class NoteInfo : MonoBehaviour
{
    [Tooltip("Tap, Hold, or Strength.")]
    public NoteType type = NoteType.Tap;

    [Tooltip("Minimum force required, normalized [0,1]. " +
             "Tap/Strength check peak force; Hold checks average force.")]
    [Range(0f, 1f)] public float requiredForce = 0.4f;

    [Tooltip("Fraction of the pickup window that must be covered/sustained. " +
             "Hold: fraction the press must overlap. " +
             "Strength: fraction force must stay >= 80% of peak. " +
             "Ignored for Tap notes.")]
    [Range(0f, 1f)] public float coverageThreshold = 0.8f;

    // ----------------------------------------------------------------
    // Chord grouping. Baked by NoteSpawner at spawn time — see
    // NoteSpawner.SpawnChord / SpawnNoteInLane.
    //
    // Consumed on BOTH sides (August 2026):
    //   GAME — tells whether a WHOLE chord was cleared; lets NoteAudioFeedback
    //          sound a chord as one musical event.
    //   DDA  — NoteHitDetector copies these onto NoteResolutionEvent, NoteResolver
    //          forwards them onto NoteOutcomeEvent, and PIDifficultyController uses
    //          chordSize to weight each failed note as 1/chordSize of an error.
    //          (Superseded the earlier "game-side only, the DDA never reads these"
    //          note, which is no longer true.)
    //
    // These are pure metadata: they change nothing about how many notes spawn,
    // their lanes, timing, type or force.
    // ----------------------------------------------------------------
    [Header("Chord grouping (set by NoteSpawner at spawn)")]
    [Tooltip("Shared id for every note spawned together in one chord. -1 for a standalone note.")]
    public int chordId = -1;

    [Tooltip("Number of notes in this note's chord (1 for a standalone note). Lets the game side " +
             "tell when every member of a chord has resolved, and lets the DDA weight a failed " +
             "chord member as 1/chordSize of an error.")]
    public int chordSize = 1;

    [Tooltip("This note's position in its chord's ONSET order: 0 = the on-beat note (the one that " +
             "coincides with the ghost beat), 1 = the next onset, and so on. 0 for a standalone " +
             "note. NOTE: this is onset order, NOT lane order — NoteSpawner shuffles lanes, so " +
             "index 0 is simply whichever lane was drawn first. With a non-zero stagger, higher " +
             "indices arrive later and (for holds, which all end together) are shorter.")]
    public int chordOnsetIndex = 0;

    [Tooltip("Onset stagger between CONSECUTIVE members of this chord, in EIGHTHS of a beat — the " +
             "GameDifficulty.chordMismatch value in force when this chord spawned. 0 = fully " +
             "synchronous. 0 for a standalone note (there is no inter-onset gap). Stored in eighths " +
             "rather than seconds so it stays tempo-independent; this note's own onset offset is " +
             "chordOnsetIndex * chordStaggerEighths / 8 beats.")]
    public int chordStaggerEighths = 0;

    /// <summary>True when this note was spawned as one of several simultaneous notes (a chord).</summary>
    public bool IsChord => chordSize > 1;
}
