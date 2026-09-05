using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The initial screen. Selects the profile that all subsequent recordings and
/// diagnostics are attributed to, and is the launch point for Play, Settings and
/// Diagnostics.
///
/// The profile list is rebuilt on every Show() (and on ProfileManager changes)
/// rather than diffed — the list is tiny and a full rebuild removes a whole class
/// of stale-row bugs.
/// </summary>
public class MainMenuScreen : UIScreen
{
    private RectTransform _profileList;
    private TMP_Text _currentLabel;

    // "New profile" form fields
    private TMP_InputField _name, _age, _state, _notes;
    private TMP_Dropdown _playingHand, _dominantHand;
    private RectTransform _form;
    private Button _newProfileBtn;

    private static readonly string[] HandOptions = { "Right", "Left" };

    protected override void Build()
    {
        var col = Panel("Rehab rhythm game", 720f, 820f);

        _currentLabel = UIFactory.Label("", col, 20, TextAlignmentOptions.Left, UIFactory.Accent);
        _currentLabel.gameObject.AddComponent<LayoutElement>().minHeight = 30f;

        UIFactory.Label("Profiles", col, 18, TextAlignmentOptions.Left, UIFactory.TextDim)
                 .gameObject.AddComponent<LayoutElement>().minHeight = 24f;

        // --- scrolling profile list ---
        var scrollHost = UIFactory.Rect("ProfileScrollHost", col);
        var le = scrollHost.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 240f;
        le.flexibleHeight = 1f;
        _profileList = UIFactory.ScrollColumn(scrollHost, 6f);

        // --- new-profile form (hidden until "Add profile" is pressed) ---
        _newProfileBtn = UIFactory.Button("+ Add profile", col, ToggleForm, UIFactory.PanelAlt);

        var formCard = UIFactory.CardColumn("NewProfileForm", col, UIFactory.PanelAlt, 6f,
                                            new RectOffset(12, 12, 12, 12));
        _form = formCard;

        _name  = UIFactory.InputField(formCard, "Name");
        _age   = UIFactory.InputField(formCard, "Age");
        _state = UIFactory.InputField(formCard, "Physical state (e.g. affected / control)");
        _notes = UIFactory.InputField(formCard,
                     "Notes (e.g. neurological damage affecting the ring finger)", "", 60f);

        UIFactory.Label("Playing hand", formCard, 14, TextAlignmentOptions.Left, UIFactory.TextDim);
        _playingHand = UIFactory.Dropdown(formCard, HandOptions, 0, null);

        UIFactory.Label("Dominant hand", formCard, 14, TextAlignmentOptions.Left, UIFactory.TextDim);
        _dominantHand = UIFactory.Dropdown(formCard, HandOptions, 0, null);

        var formRow = UIFactory.Row(formCard, 8f);
        UIFactory.Button("Cancel", formRow.transform, ToggleForm, UIFactory.PanelAlt);
        UIFactory.Button("Create", formRow.transform, CreateProfile, UIFactory.Success);

        _form.gameObject.SetActive(false);

        UIFactory.Spacer(col, 8f);

        // --- navigation ---
        var navRow = UIFactory.Row(col, 10f, 52f);
        UIFactory.Button("Settings",    navRow.transform, () => Manager.ShowSettings(),    UIFactory.PanelAlt, 18);
        UIFactory.Button("Diagnostics", navRow.transform, () => Manager.ShowDiagnostics(), UIFactory.PanelAlt, 18);
        UIFactory.Button("Device",      navRow.transform, () => Manager.ShowDevice(),      UIFactory.PanelAlt, 18);

        UIFactory.Button("Play", col, () => Manager.StartGame(), UIFactory.Accent, 26, 60f);
    }

    protected override void OnShow()
    {
        _form.gameObject.SetActive(false);
        RefreshList();
    }

    private void ToggleForm()
    {
        bool show = !_form.gameObject.activeSelf;
        _form.gameObject.SetActive(show);
        UIFactory.SetButtonText(_newProfileBtn, show ? "- Hide form" : "+ Add profile");
        if (show)
        {
            _name.text = _age.text = _state.text = _notes.text = "";
            _playingHand.SetValueWithoutNotify(0);
            _playingHand.RefreshShownValue();
            _dominantHand.SetValueWithoutNotify(0);
            _dominantHand.RefreshShownValue();
        }
    }

    private void CreateProfile()
    {
        if (string.IsNullOrWhiteSpace(_name.text))
        {
            _name.placeholder.GetComponent<TMP_Text>().text = "Name is required";
            _name.placeholder.color = UIFactory.Danger;
            return;
        }

        var p = ProfileManager.Create(_name.text, _age.text, _state.text, _notes.text,
                                       HandOptions[_playingHand.value], HandOptions[_dominantHand.value]);
        ProfileManager.Select(p);          // newly created profile becomes the active one
        _form.gameObject.SetActive(false);
        UIFactory.SetButtonText(_newProfileBtn, "+ Add profile");
        RefreshList();
    }

    private void RefreshList()
    {
        foreach (Transform child in _profileList) Object.Destroy(child.gameObject);

        var current = ProfileManager.Current;
        _currentLabel.text = $"Recording as:  <b>{ProfileManager.Describe(current)}</b>";

        foreach (var p in ProfileManager.Profiles)
        {
            bool selected = current != null && p.id == current.id;
            var captured = p;

            var row = UIFactory.CardRow("ProfileRow", _profileList,
                                        selected ? UIFactory.Accent : UIFactory.PanelAlt, 60f);

            // Clicking anywhere on the row selects the profile.
            var selectBtn = row.gameObject.AddComponent<Button>();
            selectBtn.targetGraphic = row.GetComponent<Image>();
            selectBtn.onClick.AddListener(() => { ProfileManager.Select(captured); RefreshList(); });

            // Text block takes all remaining width; the delete button is fixed-width.
            var textCol = UIFactory.Column(row, 0f);
            var textLE = textCol.gameObject.AddComponent<LayoutElement>();
            textLE.flexibleWidth = 1f;

            var title = UIFactory.Label(p.name, textCol.transform, 19, TextAlignmentOptions.Left);
            title.gameObject.AddComponent<LayoutElement>().minHeight = 24f;

            string sub = string.IsNullOrWhiteSpace(p.notes)
                ? ProfileManager.Describe(p)
                : $"{ProfileManager.Describe(p)} - {p.notes}";
            var subT = UIFactory.Label(sub, textCol.transform, 14, TextAlignmentOptions.Left,
                                       selected ? UIFactory.TextMain : UIFactory.TextDim);
            subT.overflowMode = TextOverflowModes.Ellipsis;
            subT.gameObject.AddComponent<LayoutElement>().minHeight = 20f;

            if (p.IsTestProfile) continue;      // built-in profile can never be deleted

            var del = UIFactory.Button("Delete", row, () => ConfirmDelete(captured),
                                       UIFactory.Danger, 15, 34f);
            var delLE = del.gameObject.GetComponent<LayoutElement>();
            delLE.preferredWidth = 90f;
            delLE.minWidth = 90f;
            delLE.flexibleWidth = 0f;
        }
    }

    private void ConfirmDelete(ProfileData p)
    {
        Manager.Confirm(
            "Delete profile?",
            $"'{p.name}' will be permanently removed.\n\n" +
            "Recorded sessions on disk are NOT deleted, but they will no longer " +
            "appear under any profile in Diagnostics.",
            "Delete", "Cancel",
            yes => { if (yes) { ProfileManager.Delete(p); RefreshList(); } });
    }
}
