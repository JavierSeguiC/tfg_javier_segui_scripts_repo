using UnityEngine;

/// <summary>
/// One per lane. Put this on the lane's pickup object (the one with the trigger
/// collider). Bridges Unity physics (OnTriggerEnter/Exit2D) and the game-side
/// event bus.
///
/// Replaces the old PickupZone, which called directly into NoteResolver.Instance.
/// Now this is purely a relay: it raises GameEvents.OnNoteEnterPickup /
/// OnNoteExitPickup. The NoteHitDetector (game) and the NoteResolver (DDA) both
/// subscribe — the latter being optional.
///
/// Requirements:
///   - This object has a Collider2D with "Is Trigger" enabled.
///   - Notes have a Collider2D, and at least one of (pickup, note) has a Rigidbody2D
///     so Unity generates trigger callbacks.
///   - Notes have a NoteInfo component.
/// </summary>
public class LanePickup : MonoBehaviour
{
    [Tooltip("Lane index this pickup represents (0..laneCount-1).")]
    public int laneIndex;

    void OnTriggerEnter2D(Collider2D other)
    {
        var info = other.GetComponent<NoteInfo>();
        if (info == null) return;
        GameEvents.RaiseNoteEnterPickup(laneIndex, info, other.gameObject, Time.time);
    }

    void OnTriggerExit2D(Collider2D other)
    {
        var info = other.GetComponent<NoteInfo>();
        if (info == null) return;
        GameEvents.RaiseNoteExitPickup(laneIndex, other.gameObject.GetInstanceID(), Time.time);
    }
}
