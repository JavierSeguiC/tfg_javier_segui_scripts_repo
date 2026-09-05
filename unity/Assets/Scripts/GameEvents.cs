using System;
using UnityEngine;

/// <summary>
/// Central event hub for GAME-side events. The DDA layer subscribes to these
/// (read-only, one-way) to observe what's happening in the game.
///
/// Mirror of DDAEventBus but the dependency direction is reversed: DDA reads
/// from here, never writes. If the DDA folder is deleted, these events fire
/// with no listeners and the game continues to function normally.
/// </summary>
public static class GameEvents
{
    /// <summary>
    /// Fired when a note physically enters a lane's pickup zone.
    /// (lane, noteInfo, noteGameObject, tEnter)
    /// </summary>
    public static event Action<int, NoteInfo, GameObject, float> OnNoteEnterPickup;

    /// <summary>
    /// Fired when a note physically leaves a lane's pickup zone.
    /// (lane, noteId, tExit)
    /// </summary>
    public static event Action<int, int, float> OnNoteExitPickup;

    /// <summary>
    /// Fired when the game has determined the outcome for a note.
    /// This is the game's authoritative resolution — DDA fuses it with input data
    /// downstream but never overrides the outcome itself.
    /// </summary>
    public static event Action<NoteResolutionEvent> OnNoteResolved;

    /// <summary>
    /// Fired every frame by NoteStatesBroadcaster for each note currently in a
    /// pickup zone. Also fires once with the conclusive succeeded/failed flag the
    /// instant the outcome is known (no grace period delay).
    ///
    /// Subscribe here for: visual feedback, score updates, force-meter UI.
    /// The DDA does NOT subscribe here.
    /// </summary>
    public static event Action<NoteStateEvent> OnNoteStateUpdate;

    public static void RaiseNoteEnterPickup(int lane, NoteInfo info, GameObject noteObj, float t)
        => OnNoteEnterPickup?.Invoke(lane, info, noteObj, t);

    public static void RaiseNoteExitPickup(int lane, int noteId, float t)
        => OnNoteExitPickup?.Invoke(lane, noteId, t);

    public static void RaiseNoteResolved(NoteResolutionEvent e)
        => OnNoteResolved?.Invoke(e);

    public static void RaiseNoteStateUpdate(NoteStateEvent e)
        => OnNoteStateUpdate?.Invoke(e);
}
