using System;

namespace DDA
{
    /// <summary>
    /// Central event hub for the DDA pipeline. Decouples producers (InputLogger,
    /// NoteResolver) from consumers (loggers, state estimator, diagnostics).
    /// All events are synchronous and fire on the Unity main thread.
    /// </summary>
    public static class DDAEventBus
    {
        // ---- Layer 1: raw input ----

        /// <summary>
        /// Fired the instant a press is detected (force crosses pressThreshold up).
        /// Carries (lane, tPress, eventId). The full InputPressEvent will follow on release.
        /// Used by NoteResolver to perform initial matching against active notes.
        /// </summary>
        public static event Action<int, float, int> OnPressBegin;

        /// <summary>
        /// Fired when a press is finalized (force drops below releaseThreshold).
        /// Carries the complete InputPressEvent with all summary stats.
        /// </summary>
        public static event Action<InputPressEvent> OnInputPress;

        // ---- Layer 2: note outcomes ----

        /// <summary>
        /// Fired when a note is resolved (collected, missed, wrong-lane, etc.).
        /// This is the main input signal for the state estimator and DDA controller.
        /// </summary>
        public static event Action<NoteOutcomeEvent> OnNoteOutcome;

        public static void RaisePressBegin(int lane, float t, int eventId)
            => OnPressBegin?.Invoke(lane, t, eventId);

        public static void RaiseInputPress(InputPressEvent e)
            => OnInputPress?.Invoke(e);

        public static void RaiseNoteOutcome(NoteOutcomeEvent e)
            => OnNoteOutcome?.Invoke(e);
    }
}
