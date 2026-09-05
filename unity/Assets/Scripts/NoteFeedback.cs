using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives all outcome-based note visuals:
///
///   IN-PICKUP TINT
///     The moment any note enters the pickup zone (first in-progress event),
///     its sprite is set to noteInPickupColor. This replaces the old lane-bar
///     highlight — the note itself lights up instead.
///
///   HOLD PROGRESS LERP
///     While a Hold or Strength note is being held, its sprite lerps from
///     noteInPickupColor toward hitColor proportional to holdProgress [0,1].
///
///   CONCLUSION FLASH
///     When any note concludes (succeeded or failed), the sprite briefly flashes
///     white (flashDuration seconds) then settles to hitColor or failColor.
///     Applies to all note types — tap and hold alike.
///
/// LOCK-OUT GUARD
///     Once a conclusive event has been received for a note, its ID is added to
///     _concluded PERMANENTLY (for the lifetime of this component / the note).
///     It is NOT released after the flash duration. This matters for hold notes
///     whose start window expired: NoteHitDetector fires the conclusive fail
///     event immediately, but NoteStatesBroadcaster keeps emitting in-progress
///     events for the same note for the rest of the (much longer) late-press
///     grace period — until OnNoteResolved finally fires. If the lock-out expired
///     after the short flash duration, those later in-progress events would
///     overwrite the fail color back to the in-pickup tint.
///     _concluded is only cleared in OnDisable (component teardown).
///
/// Subscribes to GameEvents.OnNoteStateUpdate (Stream 2 — instant, no grace delay).
/// The DDA does NOT interact with this script.
/// </summary>
public class NoteFeedback : MonoBehaviour
{
    [Header("Outcome colors")]
    public Color hitColor  = new Color(0.69f, 0.99f, 0.35f);  // green
    public Color failColor = new Color(0.99f, 0.43f, 0.25f);  // red/orange

    [Header("In-pickup tint")]
    [Tooltip("Color applied to any note the moment it enters the pickup zone. " +
             "For hold notes the progress lerp runs from this color to hitColor.")]
    public Color noteInPickupColor = new Color(1f, 1f, 0.6f, 1f);  // soft white-yellow

    [Header("Conclusion flash")]
    public Color flashColor = Color.white;
    [Tooltip("Seconds the white flash lasts before the final color settles in.")]
    public float flashDuration = 0.12f;

    // Tracks whether we have already applied the in-pickup tint for a note.
    // Once set, the value is noteInPickupColor (used as the lerp start for holds).
    private readonly HashSet<int> _tinted = new HashSet<int>();

    // IDs of notes that have already received a conclusive event.
    private readonly HashSet<int> _concluded = new HashSet<int>();

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------

    void OnEnable()  => GameEvents.OnNoteStateUpdate += HandleNoteStateUpdate;

    void OnDisable()
    {
        GameEvents.OnNoteStateUpdate -= HandleNoteStateUpdate;
        _tinted.Clear();
        _concluded.Clear();
    }

    // ----------------------------------------------------------------
    // Event handler
    // ----------------------------------------------------------------

    void HandleNoteStateUpdate(NoteStateEvent e)
    {
        if (e.noteObj == null) return;
        if (!e.noteObj.TryGetComponent(out SpriteRenderer sr)) return;

        if (e.succeeded || e.failed)
        {
            _concluded.Add(e.noteId);
            _tinted.Remove(e.noteId);

            Color finalColor = e.succeeded ? hitColor : failColor;
            StartCoroutine(FlashThenSetColor(sr, e.noteId, finalColor));
        }
        else
        {
            if (_concluded.Contains(e.noteId)) return;

            // First in-progress event: apply the in-pickup tint regardless of note type.
            if (!_tinted.Contains(e.noteId))
            {
                sr.color = noteInPickupColor;
                _tinted.Add(e.noteId);
            }

            // Hold/Strength: lerp from noteInPickupColor toward hitColor as the
            // player holds. Tap notes do nothing further until conclusion.
            if (e.type == NoteType.Hold || e.type == NoteType.Strength)
            {
                sr.color = Color.Lerp(noteInPickupColor, hitColor, e.holdProgress);
            }
        }
    }

    // ----------------------------------------------------------------
    // Flash coroutine
    // ----------------------------------------------------------------

    IEnumerator FlashThenSetColor(SpriteRenderer sr, int noteId, Color finalColor)
    {
        sr.color = flashColor;
        yield return new WaitForSeconds(flashDuration);
        if (sr != null)
            sr.color = finalColor;

        // Lock-out is intentionally NOT released here — see LOCK-OUT GUARD note
        // in the class summary above. _concluded stays set for this note's
        // lifetime so a still-tracking NoteStatesBroadcaster (e.g. during a hold's
        // late-press grace period) can never overwrite the settled outcome color.
    }
}
