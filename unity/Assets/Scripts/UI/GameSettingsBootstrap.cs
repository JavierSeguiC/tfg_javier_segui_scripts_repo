using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Registers the GAME-side settings. This is the file you edit to add a knob:
/// one Register*() call and the settings menu picks it up automatically — no UI
/// code, no scene wiring.
///
/// DDA-side settings (controller selection) are registered separately by
/// MenuDDABridge inside the DDA folder, so deleting that folder removes those
/// settings cleanly instead of leaving dead entries.
///
/// ORDER: runs from UIManager.Awake() before SettingsRegistry.Load(), so every
/// definition exists by the time stored values are read back.
/// </summary>
public static class GameSettingsBootstrap
{
    public const string DEV_MODE_ID = "dev.mode";

    private static bool _done;

    /// <summary>Extra GameObjects toggled by Development Mode (assigned on UIManager).</summary>
    public static List<GameObject> devModeTargets = new List<GameObject>();

    // SettingsRegistry now clears its statics each Play (domain-reload-off safety). This
    // guard must reset in lockstep, or on the second run RegisterAll() would early-out on
    // the stale _done=true and never re-register the game settings into the freshly-cleared
    // registry — leaving the menu with only the DDA controller entry. Also drop stale
    // destroyed GameObjects from devModeTargets.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        _done = false;
        devModeTargets = new List<GameObject>();
    }

    public static void RegisterAll()
    {
        if (_done) return;
        _done = true;

        // ---------------- Gameplay ----------------
        SettingsRegistry.RegisterFloat(
            "game.simultaneousChance", "Chord chance", "Gameplay",
            0.25f, 0f, 1f,
            v => { if (GameDifficulty.Instance != null)
                       GameDifficulty.Instance.simultaneousChance = (float)v; },
            "Probability a spawn becomes a chord instead of a single note.");

        SettingsRegistry.RegisterFloat(
            "game.holdNoteChance", "Hold note chance", "Gameplay",
            0.5f, 0f, 1f,
            v => { if (GameDifficulty.Instance != null)
                       GameDifficulty.Instance.holdNoteChance = (float)v; },
            "Probability a spawn is a hold note instead of a tap.");

        SettingsRegistry.RegisterFloat(
            "game.holdBeatsMin", "Hold length min (beats)", "Gameplay",
            0.5f, 0.25f, 8f,
            v => { if (GameDifficulty.Instance != null)
                       GameDifficulty.Instance.holdBeatsMin = (float)v; },
            "Shortest hold note duration in beats.");

        SettingsRegistry.RegisterFloat(
            "game.holdBeatsMax", "Hold length max (beats)", "Gameplay",
            3f, 0.5f, 12f,
            v => { if (GameDifficulty.Instance != null)
                       GameDifficulty.Instance.holdBeatsMax = (float)v; },
            "Longest hold note duration in beats.");

        // ---------------- Audio ----------------
        // SoundSystem refreshes the music channel from these every Update, and
        // multiplies SFX at play time, so writing the fields is enough.
        SettingsRegistry.RegisterFloat(
            "audio.master", "Master volume", "Audio", 1f, 0f, 1f,
            v => { if (SoundSystem.Instance != null) SoundSystem.Instance.masterVolume = (float)v; });

        SettingsRegistry.RegisterFloat(
            "audio.sfx", "SFX volume", "Audio", 1f, 0f, 1f,
            v => { if (SoundSystem.Instance != null) SoundSystem.Instance.sfxVolume = (float)v; });

        SettingsRegistry.RegisterFloat(
            "audio.music", "Music volume", "Audio", 1f, 0f, 1f,
            v => { if (SoundSystem.Instance != null) SoundSystem.Instance.musicVolume = (float)v; });

        // ---------------- Development ----------------
        SettingsRegistry.RegisterBool(
            DEV_MODE_ID, "Development mode", "Development", false,
            v => ApplyDevMode((bool)v),
            "Shows the DDA tuning/debug overlays and the difficulty preset buttons.");
    }

    /// <summary>
    /// Game-side half of dev mode: show/hide any GameObjects listed on UIManager
    /// (e.g. the PITuningHUD object). The DDA-side half — RuleBasedDDAController's
    /// IMGUI panel and the preset buttons — is handled by MenuDDABridge.
    /// </summary>
    private static void ApplyDevMode(bool on)
    {
        if (devModeTargets == null) return;
        foreach (var go in devModeTargets)
            if (go != null) go.SetActive(on);
    }
}
