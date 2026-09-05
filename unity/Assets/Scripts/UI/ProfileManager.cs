using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Persistent patient / testing profiles.
///
/// One JSON file per profile in Application.persistentDataPath/Profiles/.
/// One file per profile (rather than one big list) means a corrupted or
/// hand-edited file loses exactly one profile, and profiles can be copied
/// between machines individually.
///
/// THE ID IS THE KEY, NOT THE NAME. Every recording is stamped with profileId
/// (a GUID), so renaming a profile later never orphans its recorded sessions.
/// The built-in test profile has a fixed, reserved id and cannot be deleted.
/// </summary>
[Serializable]
public class ProfileData
{
    public string id = "";
    public string name = "";
    public string age = "";
    public string physicalState = "";
    public string notes = "";
    public string createdIso = "";

    // Which hand the player plays with, and which hand is dominant.
    // "Left"/"Right". PlayingHand drives the lane→finger remap (see
    // load_recording.m); DominantHand is purely descriptive metadata for
    // future dominant-vs-non-dominant analysis and does not affect the
    // remap. Default "Right" so existing profiles (loaded from disk
    // before this field existed) come back as the canonical convention.
    public string playingHand = "Right";
    public string dominantHand = "Right";

    public bool IsTestProfile => id == ProfileManager.TEST_PROFILE_ID;
}

public static class ProfileManager
{
    public const string TEST_PROFILE_ID = "00000000-0000-0000-0000-000000000000";

    private const string FOLDER = "Profiles";

    private static readonly List<ProfileData> _profiles = new List<ProfileData>();
    private static bool _loaded;

    /// <summary>Fired when a profile is added, deleted, or the selection changes.</summary>
    public static event Action OnProfilesChanged;

    public static IReadOnlyList<ProfileData> Profiles { get { EnsureLoaded(); return _profiles; } }
    public static ProfileData Current { get; private set; }

    private static string Dir => Path.Combine(Application.persistentDataPath, FOLDER);

    // ================================================================
    // Load / save
    // ================================================================

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _profiles.Clear();

        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var path in Directory.GetFiles(Dir, "*.json"))
            {
                try
                {
                    var p = JsonUtility.FromJson<ProfileData>(File.ReadAllText(path));
                    if (p != null && !string.IsNullOrEmpty(p.id)) _profiles.Add(p);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ProfileManager] Skipped unreadable profile " +
                                     $"'{Path.GetFileName(path)}': {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ProfileManager] Load failed: {e.Message}");
        }

        // The test profile always exists and is always first.
        var test = _profiles.Find(p => p.id == TEST_PROFILE_ID);
        if (test == null)
        {
            test = new ProfileData
            {
                id = TEST_PROFILE_ID,
                name = "Test",
                physicalState = "n/a",
                notes = "Built-in testing profile.",
                createdIso = DateTime.Now.ToString("s")
            };
            _profiles.Insert(0, test);
            SaveProfile(test);
        }
        else
        {
            _profiles.Remove(test);
            _profiles.Insert(0, test);
        }

        // Default selection: the test profile, per the design.
        if (Current == null) Current = test;
    }

    private static void SaveProfile(ProfileData p)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, p.id + ".json"), JsonUtility.ToJson(p, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ProfileManager] Save failed for '{p.name}': {e.Message}");
        }
    }

    // ================================================================
    // Mutations
    // ================================================================

    public static ProfileData Create(string name, string age, string physicalState, string notes,
                                      string playingHand = "Right", string dominantHand = "Right")
    {
        EnsureLoaded();

        var p = new ProfileData
        {
            id = Guid.NewGuid().ToString(),
            name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim(),
            age = age?.Trim() ?? "",
            physicalState = physicalState?.Trim() ?? "",
            notes = notes?.Trim() ?? "",
            playingHand = string.IsNullOrWhiteSpace(playingHand) ? "Right" : playingHand.Trim(),
            dominantHand = string.IsNullOrWhiteSpace(dominantHand) ? "Right" : dominantHand.Trim(),
            createdIso = DateTime.Now.ToString("s")
        };

        _profiles.Add(p);
        SaveProfile(p);
        OnProfilesChanged?.Invoke();
        return p;
    }

    public static void Select(ProfileData p)
    {
        EnsureLoaded();
        if (p == null) return;
        Current = p;
        OnProfilesChanged?.Invoke();
    }

    public static bool Delete(ProfileData p)
    {
        EnsureLoaded();
        if (p == null || p.IsTestProfile) return false;   // test profile is permanent

        try { File.Delete(Path.Combine(Dir, p.id + ".json")); } catch { }
        _profiles.Remove(p);

        if (Current == p) Current = _profiles.Find(x => x.id == TEST_PROFILE_ID);
        OnProfilesChanged?.Invoke();
        return true;
    }

    /// <summary>One-line description used in the profile list and headers.</summary>
    public static string Describe(ProfileData p)
    {
        if (p == null) return "(no profile)";
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.age)) bits.Add(p.age);
        if (!string.IsNullOrWhiteSpace(p.physicalState)) bits.Add(p.physicalState);
        bits.Add($"plays {p.playingHand}");
        return bits.Count > 0 ? $"{p.name} - {string.Join(", ", bits)}" : p.name;
    }
}
