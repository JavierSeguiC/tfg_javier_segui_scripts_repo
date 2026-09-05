using UnityEngine;

/// <summary>
/// Moves a note left at a configurable speed and despawns it once it leaves
/// the playfield. This is the ONLY despawn path for notes — they are not
/// destroyed by hit-detection or DDA scripts.
///
/// Speed is read from GameDifficulty when present, otherwise falls back to
/// the local default below.
/// </summary>
public class NoteMover : MonoBehaviour
{
    [Tooltip("Used only if no GameDifficulty in scene.")]
    public float fallbackSpeed = 5f;

    [Tooltip("X position past which the note is destroyed. Set far enough left " +
             "that the note's pickup window finishes resolving (incl. lateGrace) " +
             "before despawn.")]
    public float destroyXPosition = -200f;

    void Update()
    {
        float speed = GameDifficulty.Instance != null
            ? GameDifficulty.Instance.noteSpeed
            : fallbackSpeed;

        transform.Translate(Vector2.left * speed * Time.deltaTime);

        if (transform.position.x < destroyXPosition)
        {
            Destroy(gameObject);
        }
    }
}
