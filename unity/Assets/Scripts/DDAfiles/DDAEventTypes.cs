using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    // NOTE: NoteType and NoteOutcome have moved out of the DDA namespace into
    // game-side GameTypes.cs (global namespace). They are conceptually game
    // data — the game decides them; the DDA reads them. This preserves the
    // "delete the DDA folder, game still compiles" invariant.

    [System.Serializable]
    public struct ForceSample
    {
        public float t;
        public float f;
        public ForceSample(float t, float f) { this.t = t; this.f = f; }
    }

    /// <summary>
    /// Layer 1 — device-layer event. Emitted by InputLogger on press release.
    /// One finger pressed and released; captures what the hand did during the press.
    /// </summary>
    public class InputPressEvent
    {
        public int eventId;
        public int lane;
        public float tPress;
        public float tRelease;
        public float duration;
        public float fMax;
        public float fAvg;
        public float fSustained80;
        public List<ForceSample> profile;
    }

    /// <summary>
    /// Layer 2 — FUSED game+input event. Emitted by NoteResolver after fusing a
    /// game-side NoteResolutionEvent with the matching DDA-side InputPressEvent
    /// (if any).
    ///
    /// The outcome itself is the GAME'S authoritative call (copied from
    /// NoteResolutionEvent). The DDA never reinterprets it; it only enriches the
    /// record with input-device statistics for downstream state estimation.
    ///
    /// Input-side fields will be NaN / null when no matching press was found
    /// (e.g. game outcome is Missed, or a press hasn't yet finalized when
    /// the resolver decides to flush).
    /// </summary>
    public class NoteOutcomeEvent
    {
        // ---- From game (NoteResolutionEvent) ----
        public int noteId;
        public NoteType type;
        public int lane;
        public NoteOutcome outcome;           // GAME'S call — authoritative
        public float tEnter;
        public float tExit;
        public float windowDuration;          // full traversal (holds: entire body + pickup)
        public float startWindowDuration;     // tap-equivalent press window for M_t computation:
                                              // (tapNoteLength + pickupLength) / noteSpeed.
                                              // Equals windowDuration for tap notes.
                                              // M_t = startWindowDuration − timingError.
        public float requiredForce;
        public float coverageThreshold;
        public float gameObservedMaxForce;
        public float gameObservedAvgForce;
        public float gameObservedCoverage;

        // ---- Chord identity (baked at spawn, forwarded unchanged) ----
        // Origin: NoteSpawner → NoteInfo → NoteResolutionEvent → here.
        // chordSize is the one the controller acts on: PIDifficultyController
        // charges a failed note 1/chordSize of an error rather than a full one,
        // so a 4-note chord with one note hit costs 0.75 instead of 3. The other
        // three fields are carried for offline analysis / CSV only.
        public int chordId = -1;         // shared across a chord; -1 if standalone
        public int chordSize = 1;        // 1 if standalone — a weight of 1/1 is the old behaviour
        public int chordOnsetIndex;      // arrival order within the chord; 0 = on-beat member
        public int chordStaggerEighths;  // inter-onset gap in eighths of a beat; 0 = synchronous

        // ---- From input (matched InputPressEvent, if any) ----
        public int? inputEventId;             // null if no matching press found
        public int pressedLane;               // -1 if no matching press
        public float tPress;                  // NaN if no matching press
        public float tRelease;                // NaN if no matching press
        public float pressDuration;           // NaN if no matching press
        public float forceMax;                // NaN if no matching press
        public float forceAvg;                // NaN if no matching press
        public float forceSustained80;        // NaN if no matching press
        public List<ForceSample> profile;     // null if no matching press

        // ---- Derived (computed during fusion) ----
        public bool correctLane;              // press.lane == note.lane
        public float timingError;             // tPress − tEnter; NaN if no matching press
        public float forceMargin;             // (peak or avg) − requiredForce; NaN if no press
        public float coverageMargin;          // gameObservedCoverage − coverageThreshold
        public bool wasSimultaneous;          // chordSize > 1 — this note was spawned as part of a chord.
    }
}
