using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Note-level audio feedback. Lives on the sound_system GameObject alongside SoundSystem
/// and routes all playback through it.
///
/// SELF-DRIVEN (as of this revision)
///   This script now LISTENS to the game's note events (GameEvents.OnNoteStateUpdate,
///   Stream 2) and decides what to play from what happened to each note — a hit plays a
///   piano note, a miss plays the wrong sound. Notes do NOT carry "their" sound; the
///   decision lives here. It is independent of the DDA: audio responds to hits/misses
///   whether or not any DDA mode is active, and the DDA no longer calls into this script.
///
/// CLIMBING SCALE (auditive reward for chaining)
///   Every consecutive HIT climbs one step up the scale, and the pitch keeps rising
///   octave after octave — it never wraps back down. Any MISS resets the climb to the
///   root. Each time the climb reaches the top of the scale (the octave), the chime
///   fires as an extra reward. Toggle with useScaleSonification (off = flat root pitch).
///
/// CHORDS (one obstacle -> one musical event)
///   A chord's notes sound a stacked-thirds chord rooted on the CURRENT scale step: the
///   first arriving note plays the root, then the 3rd, 5th and 7th (up to 4 notes). The
///   whole chord counts as ONE step of the climb — the next note after the chord is the
///   next scale degree. A chord clears (advancing the climb, chiming if it completes the
///   scale) only when every member was hit; if any member is missed the chord resets the
///   climb, same as a single miss.
///
/// HELD NOTES
///   A held note (or held chord tone) rings a sustained voice — attack -> looped sustain ->
///   release — for as long as it is held. See the envelope clips below.
///
/// Access via NoteAudioFeedback.Instance.
/// </summary>
public class NoteAudioFeedback : MonoBehaviour
{
    public static NoteAudioFeedback Instance { get; private set; }

    [Header("Note SFX clips")]
    public AudioClip hitClip;
    [Tooltip("Volume trim for tap-note hits. Multiplies on top of the SoundSystem's SFX/master " +
             "volume — use it to balance taps against the held-note stages.")]
    [Range(0f, 1f)] public float tapVolume = 1f;
    public AudioClip wrongClip;
    [Tooltip("Plays once each time the climbing scale reaches its top (the octave).")]
    public AudioClip chimeClip;

    [Header("Held-note envelope (attack / sustain loop / release)")]
    [Tooltip("Optional one-shot ONSET TRANSIENT played the instant a hold starts (the pluck / " +
             "hammer strike / bow scrape). Leave empty to start straight into the sustain loop, " +
             "or assign hitClip here so a hold's onset matches the tap sound.")]
    public AudioClip holdAttackClip;

    [Tooltip("Looping steady-state BODY of a held note, recorded at the root pitch. This clip must " +
             "be a seamless loop: trim both ends to zero-crossings so it repeats without a click, " +
             "and set its import Load Type to 'Decompress On Load' (PCM) so Unity loops it gap-free. " +
             "Falls back to looping the hit clip if left empty.")]
    public AudioClip holdSustainClip;

    [Tooltip("Optional one-shot RELEASE TAIL played when a hold is released cleanly (the natural " +
             "decay after note-off). Leave empty for an instant cut.")]
    public AudioClip holdReleaseClip;

    [Tooltip("Per-stage volume trims so the three clips balance against each other. These multiply " +
             "on top of the SoundSystem's SFX/master volume — tweak them live in Play mode to match " +
             "the attack, sustain and tail levels.")]
    [Range(0f, 1f)] public float attackVolume  = 1f;
    [Range(0f, 1f)] public float sustainVolume = 1f;
    [Range(0f, 1f)] public float releaseVolume = 1f;

    [Header("Scale")]
    [Tooltip("When on, consecutive hits climb the scale and completed scales chime. When off, " +
             "every note plays flat at the root pitch with no chaining or chime.")]
    public bool useScaleSonification = true;

    [Tooltip("Base playback pitch = the root of the scale.")]
    public float basePitch = 1f;

    [Tooltip("Semitone offset of each scale degree from the root. The LAST entry is the octave " +
             "(one full period): the climb repeats every (length-1) steps, transposed up by that " +
             "octave span each time, so it rises forever. Default = C major, 8 entries incl. the " +
             "octave; the two half-steps fall at E->F and B->C. Edit for a different mode/scale.")]
    public int[] scaleSemitones = { 0, 2, 4, 5, 7, 9, 11, 12 }; // C  D  E  F  G  A  B  C

    [Header("Chime octave climb")]
    [Tooltip("The chime clip sits a bit low, so the first chime after a reset is pitched this many " +
             "octaves above the file (1 = one octave up: file's C4 -> C5). Each further scale " +
             "completed WITHOUT a mistake climbs one more octave (C5 -> C6 -> C7 ...); any miss " +
             "resets it back to this, the same way a miss resets the scale climb.")]
    [Min(0)] public int chimeStartOctave = 1;

    [Tooltip("Ceiling on the climb so a long clean streak doesn't pitch the chime into aliasing / " +
             "dog-whistle territory (AudioSource pitch shifts by playback rate, so each octave also " +
             "halves the clip length). Octaves above the file for the highest chime.")]
    [Min(0)] public int chimeMaxOctave = 5;

    [Header("Constant-length pitch (time-stretch)")]
    [Tooltip("When on, tap notes and the chime keep their ORIGINAL duration no matter how far the " +
             "pitch is shifted, instead of getting shorter as they rise. Done by time-stretching the " +
             "clip before the resample cancels the length change. Costs a little CPU/memory (shifted " +
             "clips are generated once and cached) and adds mild time-stretch artifacts; turn off to " +
             "go back to plain varispeed (pitch and length locked together).")]
    public bool preserveNoteLength = true;

    [Tooltip("Above this shift, fall back to plain varispeed (clip gets shorter). Bounds the clip " +
             "cache and avoids extreme, ugly stretches on the very high end of an endless climb.")]
    [Min(1)] public int maxPreserveSemitones = 48;   // 4 octaves

    // ---------------- runtime state ----------------
    int _chainStep;                                             // current step of the endless climb (0 = root)
    int _chimeOctaves;                                          // octave shift for the NEXT chime (reset -> chimeStartOctave)
    // Cache of length-preserved clips: source clip -> (cents shift -> stretched clip).
    readonly Dictionary<AudioClip, Dictionary<int, AudioClip>> _stretchCache =
        new Dictionary<AudioClip, Dictionary<int, AudioClip>>();
    readonly Dictionary<int, int>        _holdHandles = new Dictionary<int, int>();   // noteId -> SoundSystem loop handle
    readonly Dictionary<int, float>      _holdPitch   = new Dictionary<int, float>(); // noteId -> pitch its sustain rings at
    readonly HashSet<int>                _concluded   = new HashSet<int>();           // noteIds whose conclusive event was handled
    readonly Dictionary<int, ChordVoice> _chords      = new Dictionary<int, ChordVoice>(); // chordId -> in-flight chord

    /// <summary>Per-chord audio state: fixes the chord's root at the step it opened on and hands
    /// out chord-tone slots (root, 3rd, 5th, 7th) in the order members arrive.</summary>
    class ChordVoice
    {
        public bool opened;
        public int  rootStep;     // _chainStep captured when the chord's first tone sounded
        public int  size;         // chordSize (members expected)
        public int  tonesStarted; // tones sounded so far -> next chord-tone slot
        public int  resolved;     // members concluded
        public bool anyFailed;
    }

    public int ScaleLength => (scaleSemitones != null && scaleSemitones.Length > 0) ? scaleSemitones.Length : 1;

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _chimeOctaves = chimeStartOctave;
    }

    void OnEnable()  => GameEvents.OnNoteStateUpdate += OnNoteState;

    void OnDisable()
    {
        GameEvents.OnNoteStateUpdate -= OnNoteState;
        StopAllHolds();
        _concluded.Clear();
        _chords.Clear();
        _chainStep = 0;
        _chimeOctaves = chimeStartOctave;
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    // ----------------------------------------------------------------
    // Event handler (Stream 2: per-frame in-progress + one conclusive event/note)
    // ----------------------------------------------------------------
    void OnNoteState(NoteStateEvent ev)
    {
        bool isHeld = ev.type == NoteType.Hold || ev.type == NoteType.Strength;

        // ---- in-progress frame: start a held note's sustain on its first pressed frame ----
        if (!ev.succeeded && !ev.failed)
        {
            if (isHeld && ev.holdProgress > 0f
                && !_holdHandles.ContainsKey(ev.noteId)
                && !_concluded.Contains(ev.noteId))
            {
                float pitch = PitchForArrival(ev.noteObj);   // opens the chord / advances the tone slot
                StartHoldVoice(ev.noteId, pitch);
            }
            return;
        }

        // ---- conclusive frame ----
        if (!_concluded.Add(ev.noteId)) return;              // one outcome per note

        NoteInfo info  = ev.noteObj != null ? ev.noteObj.GetComponent<NoteInfo>() : null;
        bool     chord = info != null && info.IsChord;

        if (!chord) { ResolveSingle(ev.noteId, ev.succeeded); return; }
        ResolveChordMember(info.chordId, info.chordSize, ev.noteId, ev.succeeded);
    }

    // ----------------------------------------------------------------
    // Single (non-chord) notes
    // ----------------------------------------------------------------
    void ResolveSingle(int noteId, bool success)
    {
        bool sustaining = _holdHandles.ContainsKey(noteId);

        if (success)
        {
            int step = _chainStep;
            if (sustaining) EndHoldVoice(noteId, playRelease: true);   // sustain already rang the tone
            else            PlayNote(PitchForStep(step));               // tap: sound the tone now

            if (useScaleSonification && StepCompletesScale(step)) PlayChime();
            AdvanceChain();
        }
        else
        {
            if (sustaining) EndHoldVoice(noteId, playRelease: false);   // cut the sustain, no tail
            PlayWrong();
            ResetChain();
        }
    }

    // ----------------------------------------------------------------
    // Chord members — the chord sounds/scores as one event
    // ----------------------------------------------------------------
    void ResolveChordMember(int chordId, int chordSize, int noteId, bool success)
    {
        ChordVoice cv = OpenChord(chordId, chordSize);
        bool sustaining = _holdHandles.ContainsKey(noteId);

        if (success)
        {
            if (sustaining) EndHoldVoice(noteId, playRelease: true);    // held tone already rang
            else            PlayNote(ChordTonePitch(cv));               // tap: sound the next chord tone
        }
        else
        {
            if (sustaining) EndHoldVoice(noteId, playRelease: false);
            PlayWrong();
            cv.anyFailed = true;
        }

        cv.resolved++;
        if (cv.resolved < cv.size) return;                             // wait for the rest of the chord

        _chords.Remove(chordId);
        if (cv.anyFailed) { ResetChain(); return; }

        // Whole chord cleared: it occupied exactly one scale step (its root).
        if (useScaleSonification && StepCompletesScale(cv.rootStep)) PlayChime();
        AdvanceChainFrom(cv.rootStep);
    }

    ChordVoice OpenChord(int chordId, int chordSize)
    {
        if (!_chords.TryGetValue(chordId, out var cv))
        {
            cv = new ChordVoice { size = Mathf.Max(1, chordSize) };
            _chords[chordId] = cv;
        }
        if (!cv.opened) { cv.opened = true; cv.rootStep = _chainStep; }
        return cv;
    }

    // Pitch for the next tone of a chord (root, 3rd, 5th, 7th, ... as diatonic thirds
    // stacked on the chord's root step), and claim that slot.
    float ChordTonePitch(ChordVoice cv)
    {
        int slot = cv.tonesStarted++;
        return PitchForStep(cv.rootStep + 2 * slot);
    }

    // Resolve the pitch a just-arriving note should ring at, opening/advancing its chord if
    // it belongs to one. Used at hold-sustain start (chords and singles alike).
    float PitchForArrival(GameObject noteObj)
    {
        NoteInfo info = noteObj != null ? noteObj.GetComponent<NoteInfo>() : null;
        if (info != null && info.IsChord)
            return ChordTonePitch(OpenChord(info.chordId, info.chordSize));
        return PitchForStep(_chainStep);
    }

    // ----------------------------------------------------------------
    // Chain (endless climb) bookkeeping
    // ----------------------------------------------------------------
    void AdvanceChain()                 { if (useScaleSonification) _chainStep++; }
    void AdvanceChainFrom(int rootStep) { if (useScaleSonification) _chainStep = rootStep + 1; }
    void ResetChain()                   { _chainStep = 0; _chimeOctaves = chimeStartOctave; }

    // ----------------------------------------------------------------
    // Scale maths — the climb rises forever (octave stacks, never wraps down)
    // ----------------------------------------------------------------
    int StepsPerOctave => Mathf.Max(1, ScaleLength - 1);   // distinct steps before the octave repeats

    float OctaveSpanSemitones =>
        (scaleSemitones != null && scaleSemitones.Length > 0) ? scaleSemitones[scaleSemitones.Length - 1] : 12f;

    float SemitonesForStep(int step)
    {
        int spo = StepsPerOctave;
        int octave = step / spo;                 // step >= 0 always (chain never goes negative)
        int degree = step % spo;
        return scaleSemitones[degree] + OctaveSpanSemitones * octave;
    }

    float PitchForStep(int step)
        => useScaleSonification ? SoundSystem.PitchFromSemitones(SemitonesForStep(step), basePitch)
                                : basePitch;

    // True on the step that lands on the octave (top of a scale) — every StepsPerOctave steps.
    bool StepCompletesScale(int step) => step > 0 && (step % StepsPerOctave == 0);

    // ----------------------------------------------------------------
    // Low-level voices (route through SoundSystem)
    // ----------------------------------------------------------------
    void PlayNote(float pitch)
    {
        PlaySfxKeepLength(hitClip, pitch, tapVolume);
    }

    void PlayWrong()
    {
        var ss = SoundSystem.Instance;
        if (ss != null) ss.PlaySfx(wrongClip, basePitch);
    }

    void PlayChime()
    {
        if (chimeClip == null) return;

        // The chime climbs one octave per consecutive completed scale, starting
        // chimeStartOctave octaves above the file. 12 semitones = a true octave
        // (pitch x2), so the chord quality of the C-minor chime is preserved.
        int oct = Mathf.Clamp(_chimeOctaves, chimeStartOctave, Mathf.Max(chimeStartOctave, chimeMaxOctave));
        float pitch = SoundSystem.PitchFromSemitones(12 * oct, basePitch);
        PlaySfxKeepLength(chimeClip, pitch, 1f);
        _chimeOctaves++;   // next consecutive completion climbs one more octave
    }

    // ----------------------------------------------------------------
    // Constant-length pitch: play a one-shot whose DURATION stays fixed while the
    // pitch changes. AudioSource.pitch is varispeed (pitch and length locked); we
    // cancel the length change by time-stretching the clip by the same ratio first,
    // so the resample lands back on the original duration at the shifted pitch.
    // ----------------------------------------------------------------
    void PlaySfxKeepLength(AudioClip clip, float pitch, float volume)
    {
        var ss = SoundSystem.Instance;
        if (ss == null || clip == null) return;
        ss.PlaySfx(LengthPreserved(clip, pitch), pitch, volume);
    }

    // Returns a clip time-stretched by 'pitch' so that PlaySfx(..., pitch) restores the
    // original length. Falls back to the raw clip when disabled, at unity pitch, or past
    // the shift ceiling. Results are cached per (clip, cents).
    AudioClip LengthPreserved(AudioClip src, float pitch)
    {
        if (!preserveNoteLength || src == null || pitch <= 0f) return src;

        int cents = Mathf.RoundToInt(1200f * Mathf.Log(pitch, 2f));
        if (cents == 0 || Mathf.Abs(cents) > maxPreserveSemitones * 100) return src;

        if (!_stretchCache.TryGetValue(src, out var byCents))
        {
            byCents = new Dictionary<int, AudioClip>();
            _stretchCache[src] = byCents;
        }
        if (!byCents.TryGetValue(cents, out var stretched))
        {
            stretched = BuildStretched(src, pitch);   // stretch factor == pitch ratio
            byCents[cents] = stretched;
        }
        return stretched;
    }

    // WSOLA time-stretch by 'alpha' at constant pitch. Grains are written every Hs
    // samples and read near every Hs/alpha samples, but each grain's exact read offset
    // is chosen (within +/-W) to best correlate with the audio already written over the
    // overlap region — keeping grains in phase so tonal content isn't cancelled. Hann-
    // windowed and normalised by the window envelope (click-free, amplitude-correct).
    // Basic but solid; for pristine quality swap for a phase vocoder / SoundTouch.
    AudioClip BuildStretched(AudioClip src, float alpha)
    {
        int ch    = Mathf.Max(1, src.channels);
        int inLen = src.samples;                       // frames per channel
        if (inLen < 32) return src;                    // too short to stretch meaningfully

        var input = new float[inLen * ch];
        src.GetData(input, 0);

        int outLen = Mathf.Max(1, Mathf.RoundToInt(inLen * alpha));
        int N  = Mathf.Min(1024, inLen);               // grain size
        int Hs = Mathf.Max(1, N / 4);                  // synthesis hop (75% overlap)
        int ov = N - Hs;                               // overlap region for phase alignment
        int W  = Hs;                                   // WSOLA search radius
        float Ha = Hs / alpha;                         // analysis hop (outLen/inLen == Hs/Ha == alpha)

        var w = new float[N];                          // Hann window
        for (int i = 0; i < N; i++)
            w[i] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (N - 1)));

        var outI = new float[outLen * ch];

        for (int c = 0; c < ch; c++)
        {
            var acc  = new float[outLen + N];          // padded so the last grain fits
            var norm = new float[outLen + N];

            for (int m = 0; m * Hs < outLen; m++)
            {
                int sStart = m * Hs;
                int ideal  = Mathf.RoundToInt(m * Ha);

                int delta = 0;
                if (m > 0 && ov > 0)
                {
                    // choose the read offset whose grain best matches what's already
                    // been written over the overlap -> grains stay phase-aligned.
                    float best = float.NegativeInfinity;
                    for (int d = -W; d <= W; d++)
                    {
                        int a = ideal + d;
                        if (a < 0 || a + ov > inLen) continue;
                        float corr = 0f;
                        for (int k = 0; k < ov; k++)
                            corr += input[(a + k) * ch + c] * acc[sStart + k];
                        if (corr > best) { best = corr; delta = d; }
                    }
                }

                int aStart = ideal + delta;
                for (int i = 0; i < N; i++)
                {
                    int ai = aStart + i;
                    float s = (ai >= 0 && ai < inLen) ? input[ai * ch + c] : 0f;
                    acc[sStart + i]  += s * w[i];
                    norm[sStart + i] += w[i];
                }
            }

            for (int n = 0; n < outLen; n++)
                outI[n * ch + c] = norm[n] > 1e-6f ? acc[n] / norm[n] : 0f;
        }

        var clip = AudioClip.Create(src.name + "_ts" + alpha.ToString("0.###"),
                                    outLen, ch, src.frequency, false);
        clip.SetData(outI, 0);
        return clip;
    }

    void StartHoldVoice(int noteId, float pitch)
    {
        var ss = SoundSystem.Instance;
        if (ss == null || _holdHandles.ContainsKey(noteId)) return;

        if (holdAttackClip != null) ss.PlaySfx(holdAttackClip, pitch, attackVolume);   // onset transient

        AudioClip loopClip = holdSustainClip != null ? holdSustainClip : hitClip;      // steady body
        int handle = ss.StartLoop(loopClip, pitch, sustainVolume);
        if (handle >= 0)
        {
            _holdHandles[noteId] = handle;
            _holdPitch[noteId]   = pitch;
        }
    }

    void EndHoldVoice(int noteId, bool playRelease)
    {
        var ss = SoundSystem.Instance;

        if (_holdHandles.TryGetValue(noteId, out int handle))
        {
            if (ss != null) ss.StopLoop(handle);
            _holdHandles.Remove(noteId);

            if (playRelease && ss != null && holdReleaseClip != null)
            {
                float pitch = _holdPitch.TryGetValue(noteId, out float p) ? p : basePitch;
                ss.PlaySfx(holdReleaseClip, pitch, releaseVolume);                      // decay tail
            }
        }
        _holdPitch.Remove(noteId);
    }

    public void StopAllHolds()
    {
        var ss = SoundSystem.Instance;
        if (ss != null)
            foreach (var kv in _holdHandles) ss.StopLoop(kv.Value);
        _holdHandles.Clear();
        _holdPitch.Clear();
    }
}
