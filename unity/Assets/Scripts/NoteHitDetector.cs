using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Game-side hit detection. Tracks each note's window from pickup-enter to
/// pickup-exit, polls input force directly from InputManagerScript, and determines
/// the final NoteOutcome — the game's authoritative judgement on what happened.
///
/// One per scene. Place on a scene GameObject (e.g. "GameSystem").
///
/// HOLD / STRENGTH START-WINDOW MECHANIC
///   Hold and Strength notes have a "start press window" equal to the tap-equivalent
///   window: (tapNoteLength + pickupLength) / noteSpeed. The player MUST begin
///   pressing within this window — the same timing challenge as a tap note.
///
///   If no press begins within startWindowEnd = tEnter + startWindowDuration:
///     - The note turns red immediately (visual failure signal).
///     - A late-grace period runs from startWindowEnd for lateGrace seconds.
///       A press arriving in that window resolves as LatePress.
///     - After lateGrace: resolved as Missed.
///     - The note is resolved early (before the trailing edge exits the pickup).
///
///   If a press DOES begin within the start window, the hold continues normally:
///   coverage and force are tracked until the trailing edge exits, and the note
///   resolves as Hit / UnderHeld / ForceInsufficient.
///
///   IMPORTANT: a press that was already active when the note entered the pickup
///   does NOT satisfy the start window for Hold/Strength notes. The player must
///   release and begin a new press after tEnter. (For Tap notes, an early press
///   within earlyWindow is still counted as an in-window press, unchanged.)
///
/// HOLD-DROP MECHANIC
///   Once the start window is passed, if force on the hold's lane drops below
///   requiredForce at any point before the trailing edge exits, the hold is
///   failed immediately as UnderHeld. The note turns red on the frame the drop
///   is detected. There is no grace period for mid-hold drops.
///
///   EXCEPTION — already complete: if heldTime has already reached
///   coverageThreshold (against the note's expected full-traversal duration,
///   computed from its collider bounds — same method NoteStatesBroadcaster
///   uses) at the moment force drops, the note is NOT failed. Force sampling
///   also freezes at that point (no further AccumulateInWindow calls), so a
///   release immediately after completion can't drag avgForce down with
///   trailing zero-force samples and flip a completed Hold to ForceInsufficient,
///   and the final coverage check uses the same frozen denominator so waiting
///   longer before physical exit can't undo the completion either. This keeps
///   NoteHitDetector's eventual Resolve() outcome consistent with
///   NoteStatesBroadcaster's earlier real-time success signal.
///
///   In both cases, the NoteResolutionEvent carries:
///     windowDuration       = full hold traversal time  (coverage denominator)
///     startWindowDuration  = tap-equivalent window      (M_t denominator)
///   For tap notes the two fields are equal.
///
/// CHORD IDENTITY PASSTHROUGH (Aug 2026): Resolve() also snapshots the note's
///   chordId / chordSize / chordOnsetIndex / chordStaggerEighths straight off
///   NoteInfo onto the event, alongside requiredForce and coverageThreshold.
///   This detector does not interpret them; it is simply the only place a
///   NoteResolutionEvent is built, and the DDA needs to know how many notes the
///   player was asked to play at once (see NoteInfo.cs and NoteResolver.cs).
///
/// Lifecycle (tap notes, unchanged):
///   1. LanePickup fires OnNoteEnterPickup → NoteTracker created.
///   2. Each frame: poll force, accumulate stats, watch for presses.
///   3. LanePickup fires OnNoteExitPickup → window closes.
///   4. Wait lateGrace seconds → Resolve.
///
/// Lifecycle (hold/strength, updated):
///   1. OnNoteEnterPickup → NoteTracker created; startWindowEnd computed.
///   2. Frames within start window: normal press/force accumulation.
///      If press detected → startWindowPassed; continue to full hold.
///   3a. Start window passes WITH press → track until OnNoteExitPickup → Resolve.
///   3b. Start window passes WITHOUT press → note turns red; enter failed-start
///       late-grace; resolve as LatePress or Missed before the note physically exits.
/// </summary>
// Runs before NoteStatesBroadcaster (DefaultExecutionOrder 0) so that an
// authoritative fail/resolve this frame is always seen by NoteFeedback's
// lock-out before NoteStatesBroadcaster can emit another in-progress event
// for the same note in the same frame.
[DefaultExecutionOrder(-10)]
public class NoteHitDetector : MonoBehaviour
{
    [Header("Lane configuration")]
    public int laneCount = 4;

    [Header("Press detection")]
    [Tooltip("Force level at which a lane is considered pressed for hit-detection.")]
    [Range(0f, 1f)] public float pressThreshold = 0.1f;

    [Header("Timing")]
    [Tooltip("Seconds before tEnter during which a press is considered too early.")]
    public float earlyWindow = 0.3f;
    [Tooltip("Seconds after tExit (or startWindowEnd for failed holds) " +
             "during which a late press can still register.")]
    public float lateGrace = 1.0f;

    [Header("Hold start-window geometry")]
    [Tooltip("World-unit length of a tap note sprite (at localScale = 1). " +
             "Combined with pickupLength to compute the tap-equivalent press window. " +
             "Current sprite size: 40 wu.")]
    public float tapNoteLength = 40f;
    [Tooltip("World-unit thickness of the pickup trigger zone. Current pickup size: 55 wu.")]
    public float pickupLength = 55f;
    [Tooltip("Fallback note speed if GameDifficulty.Instance is null at note-enter time.")]
    public float fallbackNoteSpeed = 300f;

    [Header("Hold-drop tolerance")]
    [Tooltip("Once a hold has begun — i.e. force has first reached requiredForce — the " +
             "player may drift this far below requiredForce without failing. Only a drop " +
             "below (requiredForce − holdDropTolerance) counts as UnderHeld. This is purely " +
             "the fail boundary and is NOT reported to the DDA; coverage still only " +
             "accumulates while force ≥ requiredForce. It also absorbs the input source's " +
             "attack ramp (force rising 0 → base at press onset), which would otherwise " +
             "trip an instant UnderHeld the moment the hold began.")]
    [Range(0f, 0.5f)] public float holdDropTolerance = 0.1f;

    // ----------------------------------------------------------------
    // Per-note state
    // ----------------------------------------------------------------

    private class NoteTracker
    {
        public int noteId;
        public NoteInfo info;
        public GameObject noteObj;
        public int lane;
        public float tEnter;
        public float tExit;
        public bool closed;
        public float closedAt;
        public bool resolved;

        // Force accumulators during window
        public float maxForce;
        public float sumForce;
        public int sampleCount;
        public float heldTime;

        // Press observations
        public bool sawCorrectPressInWindow;
        public float firstCorrectPressTime;
        public bool sawWrongLanePress;
        public int wrongLane;
        public float firstWrongPressTime;
        public bool sawEarlyPress;
        public float earlyPressStartedAt;
        public bool sawLatePress;
        public float latePressTime;

        // --- Start-window mechanic (Hold / Strength only) ---
        // For tap notes startWindowDuration == windowDuration; the flags stay false.
        public float startWindowDuration; // tap-equivalent window = L_eff / noteSpeed
        public float startWindowEnd;      // tEnter + startWindowDuration
        public bool  startWindowPassed;   // press detected within start window → continue hold
        public bool  startWindowExpired;  // start window closed with no press → fail early

        // --- Hold-drop mechanic (Hold / Strength only) ---
        // Latches true the first frame force reaches requiredForce after the hold's
        // start window is passed. The hold-drop check is INERT until this is set, so
        // the press's attack ramp (force rising 0 → base over the input source's rise
        // time) can climb through the threshold without tripping an instant UnderHeld.
        // In binary keyboard mode force jumps straight to 1.0, so this latches on the
        // first frame and behaviour is unchanged.
        public bool  holdArmed;

        // Set the frame force drops below (requiredForce − holdDropTolerance) after the
        // hold has been armed. Note is resolved immediately as UnderHeld when this is true.
        public bool  holdDropped;

        // Set once heldTime first reaches coverageThreshold. From that point on,
        // AccumulateInWindow stops sampling force (freezing maxForce/avgForce at
        // their value at completion) so a post-completion release doesn't drag
        // avgForce down with trailing zero-force samples and flip a Hold from
        // Hit to ForceInsufficient.
        public bool  holdCompleted;

        // Expected full pickup-traversal duration, computed once at entry from the
        // note's collider bounds — same method NoteStatesBroadcaster uses. Lets the
        // hold-drop check recognise when enough heldTime has already accumulated to
        // satisfy coverageThreshold, so a release AFTER completion is not punished.
        public float expectedWindowDuration;
    }

    private readonly List<NoteTracker> active = new List<NoteTracker>();

    // Per-lane press state
    private bool[]  isPressed;
    private bool[]  wasPressed;
    private float[] pressStartTime;

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------

    void Awake()
    {
        isPressed     = new bool[laneCount];
        wasPressed    = new bool[laneCount];
        pressStartTime = new float[laneCount];
        for (int i = 0; i < laneCount; i++) pressStartTime[i] = -1f;
    }

    void OnEnable()
    {
        GameEvents.OnNoteEnterPickup += HandleNoteEnter;
        GameEvents.OnNoteExitPickup  += HandleNoteExit;
    }

    void OnDisable()
    {
        GameEvents.OnNoteEnterPickup -= HandleNoteEnter;
        GameEvents.OnNoteExitPickup  -= HandleNoteExit;
    }

    // ----------------------------------------------------------------
    // Pickup callbacks
    // ----------------------------------------------------------------

    void HandleNoteEnter(int lane, NoteInfo info, GameObject noteObj, float t)
    {
        // Tap-equivalent press window = L_eff / noteSpeed.
        float speed  = GameDifficulty.Instance != null
                     ? GameDifficulty.Instance.noteSpeed
                     : fallbackNoteSpeed;
        if (speed <= 0f) speed = fallbackNoteSpeed;
        float lEff   = tapNoteLength + pickupLength;
        float swDur  = lEff / speed;

        // Full pickup-traversal duration for THIS note, from its actual collider
        // bounds — mirrors NoteStatesBroadcaster.ComputeExpectedWindowDuration so
        // both systems agree on what "coverage" means for hold/strength notes.
        float expectedDur = ComputeExpectedWindowDuration(noteObj, speed);

        var tracker = new NoteTracker
        {
            noteId            = noteObj.GetInstanceID(),
            info              = info,
            noteObj           = noteObj,
            lane              = lane,
            tEnter            = t,
            tExit             = -1f,
            closed            = false,
            resolved          = false,
            wrongLane         = -1,
            startWindowDuration = swDur,
            startWindowEnd    = t + swDur,
            expectedWindowDuration = expectedDur,
            // Taps: start window == full window; mark as passed immediately so
            // the expiry logic is never entered for tap notes.
            startWindowPassed = (info.type == NoteType.Tap),
        };

        // EarlyPress: was this lane already pressed just before the note arrived?
        // For tap notes this is flagged and used in outcome classification.
        // For hold/strength notes the start window still requires a NEW press beginning
        // after tEnter — a pre-existing hold does NOT satisfy startWindowPassed.
        if (lane >= 0 && lane < laneCount && isPressed[lane] && pressStartTime[lane] >= 0f)
        {
            float pressAge = t - pressStartTime[lane];
            if (pressAge <= earlyWindow)
            {
                tracker.sawEarlyPress       = true;
                tracker.earlyPressStartedAt = pressStartTime[lane];
                // Tap notes: early press is treated as in-window in DetermineOutcome.
                // Hold/Strength: start window is NOT satisfied — must release and re-press.
                if (info.type == NoteType.Tap)
                    tracker.startWindowPassed = true;
            }
        }

        active.Add(tracker);
    }

    void HandleNoteExit(int lane, int noteId, float t)
    {
        var n = FindActive(noteId);
        if (n == null || n.resolved) return;
        // If this hold was already failed-start (startWindowExpired), the note
        // was resolved early; ignore the physical exit.
        if (n.startWindowExpired) return;
        n.tExit    = t;
        n.closed   = true;
        n.closedAt = Time.time;
    }

    // ----------------------------------------------------------------
    // Per-frame update
    // ----------------------------------------------------------------

    void Update()
    {
        float now = Time.time;

        // 1. Update per-lane press state.
        for (int lane = 0; lane < laneCount; lane++)
        {
            wasPressed[lane] = isPressed[lane];
            float force = InputManagerScript.Instance != null
                ? InputManagerScript.Instance.GetForceForLane(lane)
                : 0f;
            isPressed[lane] = force >= pressThreshold;

            if ( isPressed[lane] && !wasPressed[lane]) pressStartTime[lane] = now;
            if (!isPressed[lane] &&  wasPressed[lane]) pressStartTime[lane] = -1f;
        }

        // 2. Walk active notes.
        for (int i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n.resolved) continue;

            // Defensive: destroyed note without a pickup-exit event.
            if (!n.closed && n.noteObj == null)
            {
                n.tExit    = now;
                n.closed   = true;
                n.closedAt = now;
            }

            // ---- Start-window expiry (Hold / Strength only) ----------------
            // Only evaluated while the start window is still undecided.
            if (!n.startWindowPassed && !n.startWindowExpired)
            {
                // For hold/strength: only a new in-window press (sawCorrectPressInWindow)
                // passes the start window. An early press (sawEarlyPress) does NOT —
                // startWindowPassed was already withheld for holds in HandleNoteEnter.
                if (n.sawCorrectPressInWindow)
                {
                    // Press arrived in time — let the note proceed as a normal hold.
                    n.startWindowPassed = true;
                }
                else if (now > n.startWindowEnd)
                {
                    // Start window expired without a press — fail the note early.
                    n.startWindowExpired = true;
                    TurnNoteRed(n);
                }
            }

            // ---- Failed-start hold: watch for late press, then resolve -----
            if (n.startWindowExpired)
            {
                if (now <= n.startWindowEnd + lateGrace)
                {
                    // Late-press window: player pressed just after the deadline.
                    if (n.lane >= 0 && n.lane < laneCount &&
                        isPressed[n.lane] && !wasPressed[n.lane])
                    {
                        n.sawLatePress  = true;
                        n.latePressTime = now;
                        n.closedAt      = now;
                        Resolve(n);
                    }
                }
                else
                {
                    // Grace expired — definitely Missed.
                    n.closedAt = now;
                    Resolve(n);
                }
                continue; // Skip normal window / grace processing.
            }

            // ---- Hold-drop check (Hold / Strength, after start window passed) -----
            // Once the hold has been ARMED (force has first reached requiredForce), a
            // drop below (requiredForce − holdDropTolerance) fails the note immediately
            // as UnderHeld — UNLESS heldTime has already reached coverageThreshold, in
            // which case the hold is already complete and releasing should NOT fail it.
            // This mirrors NoteStatesBroadcaster's success check so both systems agree:
            // a release right after (or at) completion is a success, not a drop.
            //
            // The arming latch is what fixes the simulator/device attack-ramp bug: the
            // start window passes as soon as force crosses pressThreshold (0.1), but the
            // ramp is still climbing toward the player's actual force at that instant.
            // Without arming, the check saw force < requiredForce during the ramp and
            // failed every hold instantly regardless of the force being applied. By
            // waiting until force has genuinely reached requiredForce once, the ramp is
            // free to climb through the threshold, and only a real drop AFTER a real hold
            // was established is punished. The tolerance band then lets a small drift or
            // tremor below requiredForce pass. Both are fail-boundary only — coverage
            // still accrues in AccumulateInWindow strictly while force ≥ requiredForce.
            if (n.startWindowPassed && !n.startWindowExpired && !n.closed &&
                (n.info.type == NoteType.Hold || n.info.type == NoteType.Strength))
            {
                float force = InputManagerScript.Instance != null
                    ? InputManagerScript.Instance.GetForceForLane(n.lane)
                    : 0f;

                // Arm on first reaching the threshold — never cleared thereafter.
                if (!n.holdArmed && force >= n.info.requiredForce)
                    n.holdArmed = true;

                float coverageSoFar = n.expectedWindowDuration > 0f
                    ? n.heldTime / n.expectedWindowDuration
                    : 0f;
                bool alreadyComplete = coverageSoFar >= n.info.coverageThreshold;
                if (alreadyComplete) n.holdCompleted = true;

                // Fail boundary sits holdDropTolerance below the threshold, floored at 0
                // (a full release always fails, since requiredForce > holdDropTolerance
                // for every operating point in use).
                float dropFloor = Mathf.Max(0f, n.info.requiredForce - holdDropTolerance);

                if (n.holdArmed && force < dropFloor && !alreadyComplete)
                {
                    n.holdDropped = true;
                    n.tExit       = now;
                    n.closed      = true;
                    n.closedAt    = now;
                    TurnNoteRed(n);
                    Resolve(n);
                    continue;
                }
            }

            // ---- Normal processing (tap notes + holds with startWindowPassed) ----

            if (!n.closed && !n.holdCompleted)
            {
                AccumulateInWindow(n, now);
            }
            else if (!n.resolved && now <= n.closedAt + lateGrace)
            {
                // Late-press window for tap notes (and holds that completed normally).
                if (n.lane >= 0 && n.lane < laneCount &&
                    isPressed[n.lane] && !wasPressed[n.lane])
                {
                    n.sawLatePress  = true;
                    n.latePressTime = now;
                    Resolve(n);
                    continue;
                }
            }

            if (n.closed && !n.resolved && now >= n.closedAt + lateGrace)
            {
                Resolve(n);
            }
        }

        // 3. Prune resolved trackers.
        active.RemoveAll(n => n.resolved && now - n.closedAt > 2f);
    }

    void AccumulateInWindow(NoteTracker n, float now)
    {
        float force = InputManagerScript.Instance != null
            ? InputManagerScript.Instance.GetForceForLane(n.lane)
            : 0f;

        if (force > n.maxForce) n.maxForce = force;
        n.sumForce    += force;
        n.sampleCount++;
        if (force >= n.info.requiredForce) n.heldTime += Time.deltaTime;

        // First correct-lane press inside the window.
        if (n.lane >= 0 && n.lane < laneCount &&
            isPressed[n.lane] && !wasPressed[n.lane] &&
            !n.sawCorrectPressInWindow)
        {
            n.sawCorrectPressInWindow = true;
            n.firstCorrectPressTime   = now;
        }

        // First wrong-lane press.
        if (!n.sawWrongLanePress)
        {
            for (int otherLane = 0; otherLane < laneCount; otherLane++)
            {
                if (otherLane == n.lane) continue;
                if (isPressed[otherLane] && !wasPressed[otherLane])
                {
                    n.sawWrongLanePress   = true;
                    n.wrongLane           = otherLane;
                    n.firstWrongPressTime = now;
                    break;
                }
            }
        }
    }

    // ----------------------------------------------------------------
    // Resolution
    // ----------------------------------------------------------------

    void Resolve(NoteTracker n)
    {
        n.resolved = true;

        float tExitForEvent;
        float winDur;

        if (n.startWindowExpired)
        {
            // Early resolution: the hold failed before the trailing edge exited.
            // Report the start window as the effective window (used for M_t).
            tExitForEvent = n.sawLatePress
                ? n.latePressTime
                : n.startWindowEnd + lateGrace;
            winDur        = n.startWindowDuration;
        }
        else
        {
            tExitForEvent = n.tExit;
            // If the hold was already marked complete (heldTime reached threshold
            // against expectedWindowDuration before physical exit), use that same
            // denominator for the final coverage check too — otherwise the real
            // elapsed window keeps growing after release while heldTime is frozen,
            // and coverage would drop back below threshold purely from waiting.
            winDur = n.holdCompleted
                ? n.expectedWindowDuration
                : Mathf.Max(0f, n.tExit - n.tEnter);
        }

        float coverage = winDur > 0f ? Mathf.Clamp01(n.heldTime / winDur) : 0f;
        float avgForce = n.sampleCount > 0 ? n.sumForce / n.sampleCount : 0f;

        NoteOutcome outcome = DetermineOutcome(n, coverage, avgForce);

        var evt = new NoteResolutionEvent
        {
            noteId               = n.noteId,
            type                 = n.info.type,
            lane                 = n.lane,
            outcome              = outcome,
            tEnter               = n.tEnter,
            tExit                = tExitForEvent,
            windowDuration       = winDur,
            startWindowDuration  = n.startWindowDuration,
            requiredForce        = n.info.requiredForce,
            coverageThreshold    = n.info.coverageThreshold,
            // Chord identity, snapshotted from NoteInfo exactly like the two fields
            // above. Baked at spawn, so it reflects the chord this note was actually
            // spawned in even if GameDifficulty.chordMismatch / maxSimultaneousLanes
            // changed while it was in flight.
            chordId              = n.info.chordId,
            chordSize            = n.info.chordSize,
            chordOnsetIndex      = n.info.chordOnsetIndex,
            chordStaggerEighths  = n.info.chordStaggerEighths,
            observedMaxForce     = n.maxForce,
            observedAvgForce     = avgForce,
            observedCoverage     = coverage,
            correctLanePressedAt = n.sawCorrectPressInWindow ? n.firstCorrectPressTime
                                 : (n.sawLatePress ? n.latePressTime : float.NaN),
            wrongLanePressed     = n.sawWrongLanePress ? n.wrongLane : -1,
            wrongLanePressedAt   = n.sawWrongLanePress ? n.firstWrongPressTime : float.NaN,
            noteObj              = n.noteObj
        };

        GameEvents.RaiseNoteResolved(evt);
    }

    NoteOutcome DetermineOutcome(NoteTracker n, float coverage, float avgForce)
    {
        // Failed-start holds: resolved early because start window expired.
        if (n.startWindowExpired)
        {
            if (n.sawLatePress)      return NoteOutcome.LatePress;
            if (n.sawWrongLanePress) return NoteOutcome.WrongLane;
            return NoteOutcome.Missed;
        }

        // Hold/Strength dropped mid-note (force dipped below threshold after start).
        if (n.holdDropped)
            return NoteOutcome.UnderHeld;

        // Hold and Strength (start window was passed — evaluate coverage + force).
        if (n.info.type == NoteType.Hold || n.info.type == NoteType.Strength)
        {
            if (!n.sawCorrectPressInWindow && !n.sawEarlyPress && !n.sawLatePress)
                return n.sawWrongLanePress ? NoteOutcome.WrongLane : NoteOutcome.Missed;

            bool covOk = coverage >= n.info.coverageThreshold;
            bool fOk   = (n.info.type == NoteType.Hold)
                       ? avgForce >= n.info.requiredForce
                       : n.maxForce >= n.info.requiredForce;

            if (!covOk) return NoteOutcome.UnderHeld;
            if (!fOk)   return NoteOutcome.ForceInsufficient;
            return NoteOutcome.Hit;
        }

        // Tap notes.
        if (n.sawCorrectPressInWindow)
            return n.maxForce >= n.info.requiredForce ? NoteOutcome.Hit : NoteOutcome.ForceInsufficient;

        if (n.sawLatePress)      return NoteOutcome.LatePress;
        if (n.sawEarlyPress)     return NoteOutcome.EarlyPress;
        if (n.sawWrongLanePress) return NoteOutcome.WrongLane;
        return NoteOutcome.Missed;
    }

    // ----------------------------------------------------------------
    // Visual feedback
    // ----------------------------------------------------------------

    void TurnNoteRed(NoteTracker n)
    {
        if (n.noteObj == null) return;

        // NOTE: we do NOT set sr.color here. NoteFeedback is the single owner of
        // note sprite color and reacts to the OnNoteStateUpdate event below. Writing
        // color directly here as well as via NoteFeedback created a race: NoteFeedback's
        // in-pickup tint (or a hold's lerp) could be applied on the very next frame by
        // NoteStatesBroadcaster before its OnNoteResolved unsubscribe took effect,
        // overwriting this direct write and leaving the note stuck lit up instead of red.

        // Fire the conclusive state-update so NoteFeedback (and score display, etc.)
        // can react. NoteFeedback locks out further in-progress events for this note
        // once it receives this.
        GameEvents.RaiseNoteStateUpdate(new NoteStateEvent
        {
            noteId       = n.noteId,
            noteObj      = n.noteObj,
            lane         = n.lane,
            type         = n.info.type,
            failed       = true,
            currentForce = 0f,
            holdProgress = 0f
        });
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    NoteTracker FindActive(int noteId)
    {
        for (int i = 0; i < active.Count; i++)
            if (active[i].noteId == noteId && !active[i].resolved) return active[i];
        return null;
    }

    /// <summary>
    /// Full pickup-traversal duration for a note, estimated from its collider
    /// bounds at entry time. MUST match NoteStatesBroadcaster.ComputeExpectedWindowDuration
    /// exactly (same formula) so both systems agree on what "coverage" means —
    /// otherwise one can consider a hold complete while the other still expects
    /// more held time, causing exactly the kind of release-timing mismatch this
    /// method exists to prevent.
    /// </summary>
    float ComputeExpectedWindowDuration(GameObject noteObj, float speed)
    {
        if (speed <= 0f) return 1f;

        var col = noteObj.GetComponent<Collider2D>();
        if (col != null)
            return col.bounds.size.x / speed;

        return noteObj.transform.localScale.x / speed;
    }
}
