using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-frame broadcaster for live note state. Completely independent of
/// NoteHitDetector — no grace period, no late-press logic.
///
/// Tracks each note from the moment it enters a pickup zone to the moment it
/// either succeeds or exits without success. While tracked:
///   - Emits a NoteStateEvent every frame with live force and hold progress.
///   - Emits a single conclusive event (succeeded=true OR failed=true) the
///     instant the outcome is clear — before the note even leaves the pickup.
///
/// Success triggers (press must have BEGUN during the note's window):
///   Tap      — the frame force on the correct lane >= requiredForce,
///              and the press started after the note entered the pickup
///   Hold     — the frame heldTime / expectedWindowDuration >= coverageThreshold,
///              where heldTime only counts input that started after entry
///   Strength — same as Hold
///
/// Exploit prevention: if the input is already active when a note enters the
/// pickup, that ongoing press is ignored until the player releases and re-presses.
/// This prevents holding a key continuously to auto-succeed every note in a lane.
///
/// Failure triggers:
///   - The note exits the pickup without having succeeded (normal flow).
///   - NoteHitDetector fires OnNoteResolved for this note before it physically
///     exits (e.g. a hold note whose start-press window expired). In that case
///     we stop tracking immediately and emit no further events — NoteHitDetector
///     already fired the authoritative conclusive NoteStateUpdate via TurnNoteRed.
///
/// After a conclusive event, the note is removed from tracking. No more
/// per-frame events are emitted for it.
///
/// NoteFeedback and ScoreManager subscribe to GameEvents.OnNoteStateUpdate.
/// The DDA does NOT subscribe here.
/// </summary>
public class NoteStatesBroadcaster : MonoBehaviour
{
    [Header("Lane configuration")]
    public int laneCount = 4;

    [Header("Press detection")]
    [Tooltip("Force level at which a lane is considered pressed for exploit-prevention purposes. " +
             "Should be the same value used by NoteHitDetector.")]
    [Range(0f, 1f)] public float pressThreshold = 0.1f;

    private class NoteState
    {
        public int noteId;
        public NoteInfo info;
        public GameObject noteObj;
        public int lane;
        public float expectedWindowDuration; // estimated from bounds / speed at entry
        public float heldTime;               // total seconds of qualifying force (post-entry only)
        public bool ignoringCurrentPress;    // true if the lane was already pressed when note entered;
                                             // cleared when force drops below pressThreshold
    }

    private readonly List<NoteState> tracked = new List<NoteState>();

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------

    void OnEnable()
    {
        GameEvents.OnNoteEnterPickup += HandleEnter;
        GameEvents.OnNoteExitPickup  += HandleExit;
        GameEvents.OnNoteResolved    += HandleResolved;   // stop tracking early-resolved notes
    }

    void OnDisable()
    {
        GameEvents.OnNoteEnterPickup -= HandleEnter;
        GameEvents.OnNoteExitPickup  -= HandleExit;
        GameEvents.OnNoteResolved    -= HandleResolved;
    }

    // ----------------------------------------------------------------
    // Pickup callbacks
    // ----------------------------------------------------------------

    void HandleEnter(int lane, NoteInfo info, GameObject noteObj, float t)
    {
        float forceAtEntry = InputManagerScript.Instance != null
            ? InputManagerScript.Instance.GetForceForLane(lane)
            : 0f;

        tracked.Add(new NoteState
        {
            noteId   = noteObj.GetInstanceID(),
            info     = info,
            noteObj  = noteObj,
            lane     = lane,
            expectedWindowDuration = ComputeExpectedWindowDuration(noteObj),
            ignoringCurrentPress   = forceAtEntry >= pressThreshold
        });
    }

    void HandleExit(int lane, int noteId, float t)
    {
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            if (tracked[i].noteId != noteId) continue;

            // Note exited without having succeeded or been early-resolved.
            EmitConclusive(tracked[i], succeeded: false);
            tracked.RemoveAt(i);
            return;
        }
        // Not found: note was already removed by HandleResolved (early resolution).
        // Nothing to do.
    }

    /// <summary>
    /// Called when NoteHitDetector has authoritatively resolved a note — which may
    /// happen BEFORE the note physically exits the pickup (e.g. a hold note whose
    /// start-press window expired). In that case we silently drop it from tracking
    /// so we stop emitting in-progress events and don't later emit a duplicate
    /// conclusive event from HandleExit.
    ///
    /// For notes that succeeded or were resolved at/after physical exit this fires
    /// after HandleExit has already removed them — the loop finds nothing, harmless.
    /// </summary>
    void HandleResolved(NoteResolutionEvent e)
    {
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            if (tracked[i].noteId != e.noteId) continue;
            // Remove silently — NoteHitDetector already fired the authoritative
            // conclusive NoteStateUpdate (via TurnNoteRed / Resolve).
            tracked.RemoveAt(i);
            return;
        }
    }

    // ----------------------------------------------------------------
    // Per-frame update
    // ----------------------------------------------------------------

    void Update()
    {
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var n = tracked[i];

            // Defensive: note destroyed without firing OnTriggerExit
            if (n.noteObj == null)
            {
                EmitConclusive(n, succeeded: false);
                tracked.RemoveAt(i);
                continue;
            }

            float force = InputManagerScript.Instance != null
                ? InputManagerScript.Instance.GetForceForLane(n.lane)
                : 0f;

            // If a pre-existing press was active on entry, wait for the player
            // to release before counting any input for this note
            if (n.ignoringCurrentPress)
            {
                if (force < pressThreshold)
                    n.ignoringCurrentPress = false;
            }

            // Only accumulate held time from input that started after note entry
            if (!n.ignoringCurrentPress && force >= n.info.requiredForce)
                n.heldTime += Time.deltaTime;

            float progress = ComputeProgress(n);

            // Check for instant success
            if (CheckSuccess(n, force, progress))
            {
                EmitConclusive(n, succeeded: true);
                tracked.RemoveAt(i);
                continue;
            }

            // Still in progress — emit live state for force meter / progress bar
            EmitInProgress(n, force, progress);
        }
    }

    // ----------------------------------------------------------------
    // Success check
    // ----------------------------------------------------------------

    bool CheckSuccess(NoteState n, float force, float progress)
    {
        switch (n.info.type)
        {
            case NoteType.Tap:
                return !n.ignoringCurrentPress && force >= n.info.requiredForce;

            case NoteType.Hold:
            case NoteType.Strength:
                return progress >= n.info.coverageThreshold;

            default:
                return false;
        }
    }

    // ----------------------------------------------------------------
    // Event emission
    // ----------------------------------------------------------------

    void EmitInProgress(NoteState n, float force, float progress)
    {
        GameEvents.RaiseNoteStateUpdate(new NoteStateEvent
        {
            noteId       = n.noteId,
            noteObj      = n.noteObj,
            lane         = n.lane,
            type         = n.info.type,
            currentForce = force,
            holdProgress = progress,
            succeeded    = false,
            failed       = false
        });
    }

    void EmitConclusive(NoteState n, bool succeeded)
    {
        float force = (!succeeded && n.noteObj != null && InputManagerScript.Instance != null)
            ? InputManagerScript.Instance.GetForceForLane(n.lane)
            : 0f;

        GameEvents.RaiseNoteStateUpdate(new NoteStateEvent
        {
            noteId       = n.noteId,
            noteObj      = n.noteObj,
            lane         = n.lane,
            type         = n.info.type,
            currentForce = force,
            holdProgress = ComputeProgress(n),
            succeeded    = succeeded,
            failed       = !succeeded
        });
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    float ComputeProgress(NoteState n)
    {
        return n.expectedWindowDuration > 0f
            ? Mathf.Clamp01(n.heldTime / n.expectedWindowDuration)
            : 0f;
    }

    float ComputeExpectedWindowDuration(GameObject noteObj)
    {
        float speed = GameDifficulty.Instance != null
            ? GameDifficulty.Instance.noteSpeed
            : 5f;

        if (speed <= 0f) return 1f;

        var col = noteObj.GetComponent<Collider2D>();
        if (col != null)
            return col.bounds.size.x / speed;

        return noteObj.transform.localScale.x / speed;
    }
}
