using System;

/// <summary>
/// GAME-SIDE FLOW HUB. Mirror of GameEvents, for menu/session lifecycle.
///
/// This is the seam that keeps the delete test intact: the UI never references
/// anything in the DDA folder. It raises these events; MenuDDABridge (inside the
/// DDA folder) subscribes and drives SessionRecorder / the controllers. Delete
/// the DDA folder and the menus still compile — the events simply fire with no
/// listeners, exactly like GameEvents.
///
/// STATE ↔ TIMESCALE
///   MainMenu / Paused → Time.timeScale = 0  (NoteSpawner's beat clock, NoteMover
///                       and NoteHitDetector all run off Time.deltaTime, so a zero
///                       timescale freezes the whole game with no per-script stop
///                       API needed)
///   Playing          → Time.timeScale = 1
/// UIManager owns the timescale; nothing else should write it.
/// </summary>
public enum GameFlowState { MainMenu, Playing, Paused }

public static class GameFlow
{
    public static GameFlowState State { get; private set; } = GameFlowState.MainMenu;

    /// <summary>Fired on every state transition, after State has been updated.</summary>
    public static event Action<GameFlowState> OnStateChanged;

    /// <summary>A new play session began (Play pressed). Start a recording here.</summary>
    public static event Action OnGameStarted;

    /// <summary>Session suspended (pause button / Esc). Pause the recording here.</summary>
    public static event Action OnGamePaused;

    /// <summary>Session resumed from pause. Resume the recording here.</summary>
    public static event Action OnGameResumed;

    /// <summary>
    /// Session finished (exited to main menu). The bool is the user's answer to
    /// "keep this recording?" — true = save, false = discard.
    /// </summary>
    public static event Action<bool> OnGameEnded;

    public static void RaiseStateChanged(GameFlowState s)
    {
        State = s;
        OnStateChanged?.Invoke(s);
    }

    public static void RaiseGameStarted() => OnGameStarted?.Invoke();
    public static void RaiseGamePaused()  => OnGamePaused?.Invoke();
    public static void RaiseGameResumed() => OnGameResumed?.Invoke();
    public static void RaiseGameEnded(bool keepRecording) => OnGameEnded?.Invoke(keepRecording);
}
