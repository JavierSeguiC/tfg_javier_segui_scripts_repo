using TMPro;
using UnityEngine;

/// <summary>
/// Tracks score, hits, and misses. Pure game feature — independent of DDA.
/// Subscribes to GameEvents.OnNoteStateUpdate (Stream 2) so updates are
/// instant, with no grace-period delay.
///
/// Only acts on conclusive events (succeeded/failed). In-progress events
/// (both flags false) are ignored.
///
/// Score is for gameplay encouragement only. It is NOT a clinical measure
/// and has no effect on the DDA control loop.
/// </summary>
public class ScoreManager : MonoBehaviour
{
    [Header("Scoring")]
    public int hitPoints  = 100;
    public int missPenalty = 50;

    [Header("Session stats (read-only in Inspector)")]
    public int totalScore  = 0;
    public int totalHits   = 0;
    public int totalMisses = 0;

    [Header("UI")]
    public TextMeshProUGUI scoreText;

    void OnEnable()  => GameEvents.OnNoteStateUpdate += HandleNoteStateUpdate;
    void OnDisable() => GameEvents.OnNoteStateUpdate -= HandleNoteStateUpdate;

    void HandleNoteStateUpdate(NoteStateEvent e)
    {
        if (e.succeeded)
        {
            totalHits++;
            totalScore += hitPoints;
            UpdateUI();
        }
        else if (e.failed)
        {
            totalMisses++;
            totalScore = Mathf.Max(0, totalScore - missPenalty);
            UpdateUI();
        }
        // In-progress events (both false) are ignored
    }

    void UpdateUI()
    {
        if (scoreText == null) return;
        scoreText.text = $"Score: {totalScore}\nHits: {totalHits}  Misses: {totalMisses}";
    }
}
