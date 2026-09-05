using UnityEngine;
using DDA;

public class DDADebugLogger : MonoBehaviour
{
    void OnEnable()
    {
        DDAEventBus.OnPressBegin  += OnPressBegin;
        DDAEventBus.OnInputPress  += OnInputPress;
        DDAEventBus.OnNoteOutcome += OnNoteOutcome;
    }

    void OnDisable()
    {
        DDAEventBus.OnPressBegin  -= OnPressBegin;
        DDAEventBus.OnInputPress  -= OnInputPress;
        DDAEventBus.OnNoteOutcome -= OnNoteOutcome;
    }

    void OnPressBegin(int lane, float t, int id) =>
        Debug.Log($"[PRESS BEGIN] lane={lane} t={t:F3} id={id}");

    void OnInputPress(InputPressEvent e) =>
        Debug.Log($"[INPUT PRESS] id={e.eventId} lane={e.lane} dur={e.duration:F3}s " +
                  $"fMax={e.fMax:F2} fAvg={e.fAvg:F2} sustained80={e.fSustained80:F3}s");

    void OnNoteOutcome(NoteOutcomeEvent e) =>
        Debug.Log($"[NOTE OUTCOME] note={e.noteId} lane={e.lane} type={e.type} " +
                  $"outcome={e.outcome} timingErr={e.timingError:F3}s " +
                  $"simultaneous={e.wasSimultaneous} pressedLane={e.pressedLane}");
}