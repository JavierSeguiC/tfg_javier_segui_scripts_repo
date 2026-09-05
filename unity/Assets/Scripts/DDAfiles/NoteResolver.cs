using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// FUSER, not a decider. Subscribes to:
    ///   - GameEvents.OnNoteResolved   (the game's authoritative outcome)
    ///   - DDAEventBus.OnInputPress    (rich input-device data)
    /// Matches each game resolution to an input press (same lane, time-overlap),
    /// then emits a single combined NoteOutcomeEvent on DDAEventBus.OnNoteOutcome.
    ///
    /// The outcome itself comes from the game. This resolver NEVER overrides it.
    /// It only enriches the record with force profile / timing detail for the
    /// downstream state estimator.
    ///
    /// CHORD IDENTITY (Aug 2026): chordId / chordSize / chordOnsetIndex /
    /// chordStaggerEighths are forwarded verbatim from the game event, under the
    /// same "the game decides, the resolver carries" contract as `outcome`.
    /// wasSimultaneous is now simply chordSize > 1 rather than an overlap estimate
    /// computed here.
    ///
    /// Why we defer: a game resolution can arrive while the matching press is
    /// still ongoing (player still holding the key when the note's window
    /// closes). Holding the resolution briefly lets the InputPressEvent finalize
    /// so we can attach full force statistics. If nothing matching shows up
    /// within fusionDeferTime, we flush anyway with input-side fields null.
    /// </summary>
    public class NoteResolver : MonoBehaviour
    {
        [Header("Matching window")]
        [Tooltip("Seconds before tEnter a press can begin and still be considered the matching press.")]
        public float earlyMatchWindow = 0.3f;
        [Tooltip("Seconds after tExit a press can begin and still be considered the matching press.")]
        public float lateMatchWindow = 1.0f;

        [Header("Deferral / pruning")]
        [Tooltip("Maximum seconds to hold a pending game resolution while waiting for the matching " +
                 "press to finalize. After this, we emit with the data we have.")]
        public float fusionDeferTime = 1.0f;
        [Tooltip("Recent presses and resolutions older than this (seconds) are pruned.")]
        public float bufferHorizon = 5f;

        // ---- Internal buffers ----
        private readonly List<InputPressEvent>      recentPresses     = new List<InputPressEvent>();
        private readonly Dictionary<int, ActivePress> activePresses   = new Dictionary<int, ActivePress>();
        private readonly List<PendingResolution>    pending           = new List<PendingResolution>();

        /// <summary>Full session log of every fused outcome. Read at session end for diagnostics.</summary>
        public readonly List<NoteOutcomeEvent> sessionLog = new List<NoteOutcomeEvent>();

        private struct ActivePress
        {
            public int lane;
            public float tPress;
        }

        private class PendingResolution
        {
            public NoteResolutionEvent gameEvent;
            public float arrivedAt;
        }

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------

        void OnEnable()
        {
            DDAEventBus.OnPressBegin  += HandlePressBegin;
            DDAEventBus.OnInputPress  += HandleInputPress;
            GameEvents.OnNoteResolved += HandleNoteResolved;
        }

        void OnDisable()
        {
            DDAEventBus.OnPressBegin  -= HandlePressBegin;
            DDAEventBus.OnInputPress  -= HandleInputPress;
            GameEvents.OnNoteResolved -= HandleNoteResolved;
        }

        void Update()
        {
            TryFlushPending();
            Prune();
        }

        // ----------------------------------------------------------------
        // Input subscriptions
        // ----------------------------------------------------------------

        void HandlePressBegin(int lane, float tPress, int eventId)
        {
            activePresses[eventId] = new ActivePress { lane = lane, tPress = tPress };
        }

        void HandleInputPress(InputPressEvent e)
        {
            activePresses.Remove(e.eventId);
            recentPresses.Add(e);
            TryFlushPending();
        }

        // ----------------------------------------------------------------
        // Game subscription
        // ----------------------------------------------------------------

        void HandleNoteResolved(NoteResolutionEvent e)
        {
            pending.Add(new PendingResolution { gameEvent = e, arrivedAt = Time.time });
            TryFlushPending();
        }

        // ----------------------------------------------------------------
        // Fusion
        // ----------------------------------------------------------------

        void TryFlushPending()
        {
            float now = Time.time;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (CanFuse(p, now))
                {
                    Fuse(p.gameEvent);
                    pending.RemoveAt(i);
                }
            }
        }

        bool CanFuse(PendingResolution p, float now)
        {
            if (now - p.arrivedAt >= fusionDeferTime) return true;

            var ge = p.gameEvent;
            foreach (var ap in activePresses.Values)
            {
                if (ap.lane != ge.lane) continue;
                if (ap.tPress >= ge.tEnter - earlyMatchWindow &&
                    ap.tPress <= ge.tExit  + lateMatchWindow)
                    return false;
            }
            return true;
        }

        void Fuse(NoteResolutionEvent ge)
        {
            InputPressEvent matched = FindMatchingPress(ge);

            if (matched == null && ge.outcome == NoteOutcome.WrongLane)
                matched = FindWrongLanePress(ge);

            var outcome = BuildOutcome(ge, matched);
            sessionLog.Add(outcome);
            DDAEventBus.RaiseNoteOutcome(outcome);
        }

        InputPressEvent FindMatchingPress(NoteResolutionEvent ge)
        {
            InputPressEvent best      = null;
            float           bestScore = float.MaxValue;
            for (int i = 0; i < recentPresses.Count; i++)
            {
                var p = recentPresses[i];
                if (p.lane != ge.lane)                        continue;
                if (p.tPress < ge.tEnter - earlyMatchWindow)  continue;
                if (p.tPress > ge.tExit  + lateMatchWindow)   continue;

                float score = Mathf.Abs(p.tPress - ge.tEnter);
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        InputPressEvent FindWrongLanePress(NoteResolutionEvent ge)
        {
            InputPressEvent best        = null;
            float           bestOverlap = 0f;
            for (int i = 0; i < recentPresses.Count; i++)
            {
                var p = recentPresses[i];
                if (p.lane == ge.lane) continue;
                float ov = Overlap(p.tPress, p.tRelease, ge.tEnter, ge.tExit);
                if (ov > bestOverlap) { bestOverlap = ov; best = p; }
            }
            return best;
        }

        NoteOutcomeEvent BuildOutcome(NoteResolutionEvent ge, InputPressEvent ie)
        {
            var ev = new NoteOutcomeEvent
            {
                // --- From game (authoritative) ---
                noteId               = ge.noteId,
                type                 = ge.type,
                lane                 = ge.lane,
                outcome              = ge.outcome,
                tEnter               = ge.tEnter,
                tExit                = ge.tExit,
                windowDuration       = ge.windowDuration,
                startWindowDuration  = ge.startWindowDuration,   // tap-equivalent; M_t uses this
                requiredForce        = ge.requiredForce,
                coverageThreshold    = ge.coverageThreshold,
                gameObservedMaxForce = ge.observedMaxForce,
                gameObservedAvgForce = ge.observedAvgForce,
                gameObservedCoverage = ge.observedCoverage,

                // --- Chord identity: forwarded verbatim, never reinterpreted ---
                // Same contract as `outcome`: the GAME decides, the resolver carries.
                // chordSize is what PIDifficultyController divides an error by.
                chordId              = ge.chordId,
                chordSize            = ge.chordSize,
                chordOnsetIndex      = ge.chordOnsetIndex,
                chordStaggerEighths  = ge.chordStaggerEighths,

                // --- Derived ---
                coverageMargin  = ge.observedCoverage - ge.coverageThreshold,
                // Authoritative chord membership, straight from the spawner's record.
                // Guarded against a malformed/legacy prefab reporting chordSize 0.
                wasSimultaneous = ge.chordSize > 1,
            };

            if (ie != null)
            {
                ev.inputEventId     = ie.eventId;
                ev.pressedLane      = ie.lane;
                ev.tPress           = ie.tPress;
                ev.tRelease         = ie.tRelease;
                ev.pressDuration    = ie.duration;
                ev.forceMax         = ie.fMax;
                ev.forceAvg         = ie.fAvg;
                ev.forceSustained80 = ie.fSustained80;
                ev.profile          = ie.profile;
                ev.correctLane      = ie.lane == ge.lane;
                ev.timingError      = ie.tPress - ge.tEnter;
                // WHICH press statistic represents "the force the player applied"
                // depends on what the note actually asked for. Deliberate per type:
                //   Tap      -> fMax. A brief, near-impulsive press; the peak IS the
                //               gesture, and the mean is diluted by rise and release.
                //   Strength -> fMax. Peak-strength note type by definition.
                //   Hold     -> fAvg. The hold-drop mechanic already fails the note the
                //               instant force dips below τ_ℓ, so fMax only reports the
                //               best instant the player ever reached — it says nothing
                //               about whether they COMFORTABLY sustained above threshold.
                //               The mean does. (fSustained80 is captured alongside as a
                //               finer endurance-quality metric, but is not the regulated
                //               signal.)
                // Keeping this as ONE scalar in forceMargin means the PI force loops
                // consume whatever lands here and need no note-type awareness at all.
                float referenceForce = (ge.type == NoteType.Hold) ? ie.fAvg : ie.fMax;
                ev.forceMargin      = referenceForce - ge.requiredForce;
            }
            else
            {
                ev.inputEventId     = null;
                ev.pressedLane      = -1;
                ev.tPress           = float.NaN;
                ev.tRelease         = float.NaN;
                ev.pressDuration    = float.NaN;
                ev.forceMax         = float.NaN;
                ev.forceAvg         = float.NaN;
                ev.forceSustained80 = float.NaN;
                ev.profile          = null;
                ev.correctLane      = false;
                ev.timingError      = float.NaN;
                ev.forceMargin      = float.NaN;
            }

            return ev;
        }

        // ----------------------------------------------------------------
        // Maintenance
        // ----------------------------------------------------------------

        static float Overlap(float aStart, float aEnd, float bStart, float bEnd)
        {
            float start = Mathf.Max(aStart, bStart);
            float end   = Mathf.Min(aEnd,   bEnd);
            return Mathf.Max(0f, end - start);
        }

        void Prune()
        {
            float cutoff = Time.time - bufferHorizon;
            recentPresses.RemoveAll(p => p.tRelease < cutoff);
        }
    }
}
