using UnityEngine;

/// <summary>
/// Central audio engine for the game. Lives on the sound_system GameObject.
///
/// Generic and gameplay-agnostic: it plays clips through pooled one-shot voices, a set
/// of looping voices, a pool of sample-accurate scheduled voices, and a music channel,
/// applying master / SFX / music volume. It knows nothing about notes, the DDA, or
/// scales. Higher-level systems (NoteAudioFeedback, BeatClock, a MusicDirector later)
/// call into it.
///
/// Access via SoundSystem.Instance.
/// </summary>
public class SoundSystem : MonoBehaviour
{
    public static SoundSystem Instance { get; private set; }

    [Header("Mixing")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float sfxVolume = 1f;
    [Range(0f, 1f)] public float musicVolume = 1f;

    [Header("Voice pools")]
    [Tooltip("Voices for one-shot SFX (round-robin).")]
    public int sfxVoices = 8;
    [Tooltip("Voices for sustained / looping SFX (held notes, drones, ...).")]
    public int loopVoices = 8;
    [Tooltip("Voices for sample-accurate scheduled SFX (metronome ticks, music stems).")]
    public int scheduledVoices = 4;

    AudioSource[] _sfx;
    int _sfxCursor;
    AudioSource[] _loops;
    bool[] _loopBusy;
    AudioSource[] _sched;
    int _schedCursor;
    AudioSource _music;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _sfx = new AudioSource[Mathf.Max(1, sfxVoices)];
        for (int i = 0; i < _sfx.Length; i++) _sfx[i] = NewSource(false);

        _loops = new AudioSource[Mathf.Max(1, loopVoices)];
        _loopBusy = new bool[_loops.Length];
        for (int i = 0; i < _loops.Length; i++) _loops[i] = NewSource(true);

        _sched = new AudioSource[Mathf.Max(1, scheduledVoices)];
        for (int i = 0; i < _sched.Length; i++) _sched[i] = NewSource(false);

        _music = NewSource(true);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    AudioSource NewSource(bool loop)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f; // 2D
        return s;
    }

    void Update()
    {
        // Keep the music channel volume live so it can be tuned at runtime.
        if (_music != null) _music.volume = musicVolume * masterVolume;
    }

    // ---------------- one-shot SFX ----------------
    /// <summary>Play a one-shot sound effect. Pitch is a multiplier (1 = original).</summary>
    public void PlaySfx(AudioClip clip, float pitch = 1f, float volume = 1f)
    {
        if (clip == null || _sfx == null) return;
        var src = _sfx[_sfxCursor];
        _sfxCursor = (_sfxCursor + 1) % _sfx.Length;
        src.pitch = pitch;
        src.PlayOneShot(clip, Mathf.Clamp01(volume) * sfxVolume * masterVolume);
    }

    // ---------------- sample-accurate scheduled SFX ----------------
    /// <summary>Play a clip at an exact AudioSettings.dspTime (schedule slightly ahead of now).
    /// Round-robins a small pool so consecutive scheduled clicks don't clobber each other.</summary>
    public void PlayScheduled(AudioClip clip, double dspTime, float pitch = 1f, float volume = 1f)
    {
        if (clip == null || _sched == null) return;
        var s = _sched[_schedCursor];
        _schedCursor = (_schedCursor + 1) % _sched.Length;
        s.clip = clip;
        s.pitch = pitch;
        s.volume = Mathf.Clamp01(volume) * sfxVolume * masterVolume;
        s.PlayScheduled(dspTime);
    }

    // ---------------- sustained / looping SFX ----------------
    /// <summary>Start a looping voice. Returns a handle to stop it later, or -1 if none free.</summary>
    public int StartLoop(AudioClip clip, float pitch = 1f, float volume = 1f)
    {
        if (clip == null || _loops == null) return -1;
        for (int i = 0; i < _loops.Length; i++)
        {
            if (_loopBusy[i]) continue;
            var s = _loops[i];
            s.clip = clip;
            s.pitch = pitch;
            s.loop = true;
            s.volume = Mathf.Clamp01(volume) * sfxVolume * masterVolume;
            s.Play();
            _loopBusy[i] = true;
            return i;
        }
        return -1; // all voices busy
    }

    /// <summary>Stop a looping voice started with StartLoop.</summary>
    public void StopLoop(int handle)
    {
        if (_loops == null || handle < 0 || handle >= _loops.Length) return;
        if (!_loopBusy[handle]) return;
        _loops[handle].Stop();
        _loops[handle].clip = null;
        _loopBusy[handle] = false;
    }

    public void StopAllLoops()
    {
        if (_loops == null) return;
        for (int i = 0; i < _loops.Length; i++) StopLoop(i);
    }

    // ---------------- music channel (ready for later) ----------------
    public void PlayMusic(AudioClip clip, bool loop = true)
    {
        if (_music == null || clip == null) return;
        _music.clip = clip;
        _music.loop = loop;
        _music.volume = musicVolume * masterVolume;
        _music.Play();
    }

    public void StopMusic() { if (_music != null) _music.Stop(); }

    public bool IsMusicPlaying => _music != null && _music.isPlaying;

    // ---------------- helpers ----------------
    /// <summary>Pitch multiplier for a shift of `semitones` from base (12 semitones = one octave).</summary>
    public static float PitchFromSemitones(float semitones, float basePitch = 1f)
        => basePitch * Mathf.Pow(2f, semitones / 12f);
}
