using UnityEngine;

/// <summary>
/// Sizes a hold note's on-screen length to a requested pickup-window DURATION.
///
/// Length is no longer randomised here. The spawner now owns hold timing,
/// because it must know each note's duration in advance to (a) keep all chord
/// notes ending together and (b) space the next spawn so lanes never overlap.
/// NoteSpawner calls SetWindowDuration() at spawn time.
///
/// The window duration is purely geometric: a note of world-length L travelling
/// at noteSpeed takes L / noteSpeed to cross any fixed point, so to make the
/// window last 'windowDurationSeconds' we set L = windowDurationSeconds * noteSpeed.
/// (This is the same relationship NoteStatesBroadcaster uses when it estimates
/// expectedWindowDuration = collider.bounds.size.x / noteSpeed.)
///
/// No reference to GameDifficulty: this component is driven entirely by the
/// spawner, so it keeps working as a passive sizer even if the difficulty
/// controller is absent.
///
/// Assumes the note is not parented under a non-uniformly-scaled transform (the
/// standard setup — notes are instantiated at the scene root). If that ever
/// changes, account for transform.parent.lossyScale.x here.
/// </summary>
public class HoldNoteScript : MonoBehaviour
{
    /// <summary>
    /// Stretches localScale.x so the note's WORLD length equals
    /// windowDurationSeconds * noteSpeed.
    /// </summary>
    public void SetWindowDuration(float windowDurationSeconds, float noteSpeed)
    {
        float worldLength = Mathf.Max(0.0001f, windowDurationSeconds * noteSpeed);

        var sr = GetComponentInChildren<SpriteRenderer>();
        if (sr == null || sr.sprite == null) return;

        // Sprite size at localScale = 1, in local units.
        float spriteLocalWidth = sr.sprite.bounds.size.x;
        if (spriteLocalWidth <= 0f) return;

        // Absolute set (not multiply): final world width == worldLength,
        // independent of the prefab's authored scale.
        Vector3 s = transform.localScale;
        s.x = worldLength / spriteLocalWidth;
        transform.localScale = s;
    }
}
