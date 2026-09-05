using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// PLACEHOLDER diagnostics: raw session history for the selected profile.
///
/// Scans Application.persistentDataPath/Recordings/ for session folders, reads
/// each sessionMeta CSV, and lists the sessions whose profileId matches the
/// currently-selected profile. No aggregation or metric computation yet — that
/// is the natural next step once there is real patient data to aggregate, and it
/// can be added here without touching anything else.
///
/// Reading sessionMeta (rather than parsing folder names) means the listing keeps
/// working if the filename convention ever changes.
/// </summary>
public class DiagnosticsScreen : UIScreen
{
    private RectTransform _list;
    private TMP_Text _header;

    private class SessionRow
    {
        public string stamp;
        public string isoTimestamp;
        public float duration;
        public int notes, presses;
        public string folder;
    }

    protected override void Build()
    {
        var col = Panel("Diagnostics", 760f, 800f);

        _header = UIFactory.Label("", col, 19, TextAlignmentOptions.Left, UIFactory.Accent);
        _header.gameObject.AddComponent<LayoutElement>().minHeight = 28f;

        var scrollHost = UIFactory.Rect("SessionScrollHost", col);
        var le = scrollHost.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 560f;
        le.flexibleHeight = 1f;
        _list = UIFactory.ScrollColumn(scrollHost, 6f);

        UIFactory.Spacer(col, 6f);

        var row = UIFactory.Row(col, 10f, 50f);
        UIFactory.Button("Open recordings folder", row.transform,
                         () => Application.OpenURL("file://" + RecordingsRoot()),
                         UIFactory.PanelAlt);
        UIFactory.Button("Back", row.transform, () => Manager.ShowMainMenu(), UIFactory.PanelAlt);
    }

    protected override void OnShow() => Refresh();

    private static string RecordingsRoot()
        => Path.Combine(Application.persistentDataPath, "Recordings");

    private void Refresh()
    {
        foreach (Transform child in _list) UnityEngine.Object.Destroy(child.gameObject);

        var profile = ProfileManager.Current;
        _header.text = $"Sessions for  <b>{ProfileManager.Describe(profile)}</b>";

        var sessions = LoadSessions(profile != null ? profile.id : null);

        if (sessions.Count == 0)
        {
            var empty = UIFactory.Label(
                "No recorded sessions for this profile yet.\n\n" +
                "Sessions are saved when you exit to the main menu and choose to keep " +
                "the recording.", _list, 17, TextAlignmentOptions.TopLeft, UIFactory.TextDim);
            empty.gameObject.AddComponent<LayoutElement>().minHeight = 100f;
            return;
        }

        var totals = UIFactory.Label(
            $"{sessions.Count} session(s) - {SumDuration(sessions):0} s total - " +
            $"{SumNotes(sessions)} notes recorded",
            _list, 16, TextAlignmentOptions.Left, UIFactory.TextDim);
        totals.gameObject.AddComponent<LayoutElement>().minHeight = 26f;

        foreach (var s in sessions)
        {
            var card = UIFactory.CardColumn("Session", _list, UIFactory.PanelAlt, 2f);

            string when = s.isoTimestamp;
            if (DateTime.TryParse(s.isoTimestamp, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var dt))
                when = dt.ToString("yyyy-MM-dd  HH:mm");

            UIFactory.Label(when, card, 18, TextAlignmentOptions.Left)
                     .gameObject.AddComponent<LayoutElement>().minHeight = 24f;

            UIFactory.Label(
                $"{s.duration:0.0} s - {s.notes} notes - {s.presses} presses - {s.stamp}",
                card, 14, TextAlignmentOptions.Left, UIFactory.TextDim)
                .gameObject.AddComponent<LayoutElement>().minHeight = 20f;
        }
    }

    private static float SumDuration(List<SessionRow> rows)
    {
        float t = 0f; foreach (var r in rows) t += r.duration; return t;
    }
    private static int SumNotes(List<SessionRow> rows)
    {
        int n = 0; foreach (var r in rows) n += r.notes; return n;
    }

    // ================================================================
    // sessionMeta scanning
    // ================================================================

    private List<SessionRow> LoadSessions(string profileId)
    {
        var results = new List<SessionRow>();
        string root = RecordingsRoot();
        if (string.IsNullOrEmpty(profileId) || !Directory.Exists(root)) return results;

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var metas = Directory.GetFiles(dir, "sessionMeta_*.csv");
                if (metas.Length == 0) continue;

                var row = ParseMeta(metas[0]);
                if (row == null) continue;
                if (GetField(metas[0], "profileId") != profileId) continue;

                row.folder = dir;
                results.Add(row);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Diagnostics] Skipped '{dir}': {e.Message}");
            }
        }

        results.Sort((a, b) => string.CompareOrdinal(b.isoTimestamp, a.isoTimestamp)); // newest first
        return results;
    }

    private SessionRow ParseMeta(string path)
    {
        var cells = ReadHeaderAndRow(path);
        if (cells == null) return null;

        return new SessionRow
        {
            stamp        = Val(cells, "sessionStamp"),
            isoTimestamp = Val(cells, "isoTimestamp"),
            duration     = ParseF(Val(cells, "activeDurationSeconds")),
            notes        = ParseI(Val(cells, "notesWritten")),
            presses      = ParseI(Val(cells, "pressesWritten")),
        };
    }

    private string GetField(string path, string column)
    {
        var cells = ReadHeaderAndRow(path);
        return cells != null ? Val(cells, column) : null;
    }

    private readonly Dictionary<string, Dictionary<string, string>> _metaCache =
        new Dictionary<string, Dictionary<string, string>>();

    private Dictionary<string, string> ReadHeaderAndRow(string path)
    {
        if (_metaCache.TryGetValue(path, out var cached)) return cached;

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return null;

        var head = SplitCsv(lines[0]);
        var vals = SplitCsv(lines[1]);

        var map = new Dictionary<string, string>();
        for (int i = 0; i < head.Count && i < vals.Count; i++) map[head[i]] = vals[i];

        _metaCache[path] = map;
        return map;
    }

    private static string Val(Dictionary<string, string> m, string k)
        => m.TryGetValue(k, out var v) ? v : "";

    private static float ParseF(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
    private static int ParseI(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    /// <summary>RFC4180-aware split — the notes field is quoted and may contain commas.</summary>
    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    protected override void OnHide() => _metaCache.Clear();   // pick up new sessions next time
}
