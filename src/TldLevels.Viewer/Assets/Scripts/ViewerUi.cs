using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace TldLevels.Viewer
{

/// <summary>
/// UI Toolkit front end for <see cref="LevelLoader"/>. The layout and styling
/// live in <c>Assets/UI/Resources/ViewerUI.uxml</c> and <c>ViewerUI.uss</c>; this
/// component binds the named elements, raises plain C# events and keeps the
/// panel readable on small Game view sizes.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class ViewerUi : MonoBehaviour
{
    private const float ReferenceWidth = 1280f;
    private const float ReferenceHeight = 720f;
    private const float MinScale = 0.8f;
    private const float MaxScale = 1.5f;

    private UIDocument document;
    private PanelSettings panelSettings;
    private VisualElement root;
    private VisualElement panel;
    private VisualElement settings;
    private VisualElement radiusRow;
    private Label levelEmpty;
    private Label statusLabel;
    private Label streamerLabel;
    private Label radiusLabel;
    private Label hintLabel;
    private Label exporterWarning;
    private TextField exportFolderField;
    private TextField gameFolderField;
    private TextField exporterPathField;
    private TextField filterField;
    private TextField packagePathField;
    private ScrollView levelList;
    private Slider radiusSlider;
    private Button rescanButton;
    private Button clearFilterButton;
    private Button loadPathButton;
    private Button frameButton;
    private Button panelToggleButton;
    private Button settingsToggleButton;

    private bool ready;
    private string lastStatus;
    private string lastHint;

    public event Action RescanRequested;
    public event Action ClearFilterRequested;
    public event Action<string> FilterChanged;
    public event Action<int> LevelSelected;
    public event Action LoadPathRequested;
    public event Action FrameRequested;
    public event Action<float> RadiusChanged;

    public string ExportFolder
    {
        get => exportFolderField != null ? exportFolderField.value : string.Empty;
        set => exportFolderField?.SetValueWithoutNotify(value ?? string.Empty);
    }

    public string GameFolder
    {
        get => gameFolderField != null ? gameFolderField.value : string.Empty;
        set => gameFolderField?.SetValueWithoutNotify(value ?? string.Empty);
    }

    public string ExporterPath
    {
        get => exporterPathField != null ? exporterPathField.value : string.Empty;
        set => exporterPathField?.SetValueWithoutNotify(value ?? string.Empty);
    }

    public string PackagePath
    {
        get => packagePathField != null ? packagePathField.value : string.Empty;
        set => packagePathField?.SetValueWithoutNotify(value ?? string.Empty);
    }

    public string Filter
    {
        get => filterField != null ? filterField.value : string.Empty;
        set => filterField?.SetValueWithoutNotify(value ?? string.Empty);
    }

    public bool IsTextInputFocused =>
        root != null && root.panel != null && root.panel.focusController.focusedElement is TextField;

    public bool IsReady => ready;

    public void Initialize()
    {
        if (ready)
        {
            return;
        }

        document = GetComponent<UIDocument>();
        if (document == null)
        {
            Debug.LogError("[viewer] UIDocument component is missing.");
            return;
        }

        panelSettings = document.panelSettings;
        if (panelSettings == null)
        {
            panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = "Viewer Panel Settings (runtime)";
            document.panelSettings = panelSettings;
        }

        if (panelSettings.themeStyleSheet == null)
        {
            panelSettings.themeStyleSheet = Resources.Load<ThemeStyleSheet>("ViewerTheme");
        }

        if (document.visualTreeAsset == null)
        {
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>("ViewerUI");
        }

        TryBind();
    }

    private void Update()
    {
        if (!ready)
        {
            TryBind();
            if (!ready)
            {
                return;
            }
        }

        UpdateScale();
    }

    private void TryBind()
    {
        if (ready || document == null)
        {
            return;
        }

        root = document.rootVisualElement;
        if (root == null || root.childCount == 0)
        {
            return;
        }

        panel = root.Q<VisualElement>("panel");
        settings = root.Q<VisualElement>("settings");
        radiusRow = root.Q<VisualElement>("radius-row");
        levelEmpty = root.Q<Label>("level-empty");
        statusLabel = root.Q<Label>("status");
        streamerLabel = root.Q<Label>("streamer-status");
        radiusLabel = root.Q<Label>("radius-label");
        hintLabel = root.Q<Label>("hint");
        exporterWarning = root.Q<Label>("exporter-warning");
        exportFolderField = root.Q<TextField>("export-folder");
        gameFolderField = root.Q<TextField>("game-folder");
        exporterPathField = root.Q<TextField>("exporter-path");
        filterField = root.Q<TextField>("filter");
        packagePathField = root.Q<TextField>("package-path");
        levelList = root.Q<ScrollView>("level-list");
        radiusSlider = root.Q<Slider>("radius");
        rescanButton = root.Q<Button>("rescan");
        clearFilterButton = root.Q<Button>("clear-filter");
        loadPathButton = root.Q<Button>("load-path");
        frameButton = root.Q<Button>("frame-level");
        panelToggleButton = root.Q<Button>("panel-toggle");
        settingsToggleButton = root.Q<Button>("settings-toggle");

        if (panel == null || levelList == null || statusLabel == null || filterField == null)
        {
            Debug.LogError("[viewer] ViewerUI.uxml is missing required elements.");
            return;
        }

        rescanButton.clicked += () => RescanRequested?.Invoke();
        clearFilterButton.clicked += () => ClearFilterRequested?.Invoke();
        filterField.RegisterValueChangedCallback(evt => FilterChanged?.Invoke(evt.newValue ?? string.Empty));
        loadPathButton.clicked += () => LoadPathRequested?.Invoke();
        frameButton.clicked += () => FrameRequested?.Invoke();
        radiusSlider.RegisterValueChangedCallback(evt => RadiusChanged?.Invoke(evt.newValue));
        panelToggleButton.clicked += TogglePanel;
        settingsToggleButton.clicked += ToggleSettings;

        SetExporterWarning(false);
        SetLevelEmpty(null);
        SetBusy(false);
        SetFrameEnabled(false);
        SetStreamer(false, string.Empty, 0f, 0f);

        ready = true;
        UpdateScale();
    }

    private void UpdateScale()
    {
        if (panelSettings == null)
        {
            return;
        }

        // Fit the panel to the viewport but never shrink the text below a
        // readable size; percentages in the USS handle the actual fit.
        float scale = Mathf.Clamp(
            Mathf.Min(Screen.width / ReferenceWidth, Screen.height / ReferenceHeight),
            MinScale,
            MaxScale);

        if (Mathf.Abs(panelSettings.scale - scale) > 0.01f)
        {
            panelSettings.scale = scale;
        }
    }

    private void TogglePanel()
    {
        bool collapsed = panel.ClassListContains("collapsed");
        panel.EnableInClassList("collapsed", !collapsed);
        panelToggleButton.text = collapsed ? "Hide" : "Show";
    }

    private void ToggleSettings()
    {
        settings.EnableInClassList("hidden", !settings.ClassListContains("hidden"));
        settingsToggleButton.text = settings.ClassListContains("hidden") ? "Settings" : "Hide settings";
    }

    public void SetLevels(IReadOnlyList<ViewerLevel> levels, bool interactable)
    {
        if (!ready)
        {
            return;
        }

        levelList.Clear();
        VisualElement row = null;
        foreach (ViewerLevel level in levels)
        {
            if (row == null)
            {
                row = new VisualElement();
                row.AddToClassList("level-row");
            }

            var button = new Button(() => LevelSelected?.Invoke(level.Id))
            {
                text = level.Label,
                tooltip = level.SourcePath,
            };
            button.AddToClassList("level-button");
            button.AddToClassList(level.Exported ? "level-exported" : "level-convert");
            row.Add(button);

            if (row.childCount == 2)
            {
                levelList.Add(row);
                row = null;
            }
        }

        if (row != null)
        {
            levelList.Add(row);
        }

        levelList.SetEnabled(interactable);
    }

    public void SetLevelEmpty(string message)
    {
        if (!ready)
        {
            return;
        }

        if (string.IsNullOrEmpty(message))
        {
            levelEmpty.EnableInClassList("hidden", true);
            return;
        }

        if (levelEmpty.text != message)
        {
            levelEmpty.text = message;
        }

        levelEmpty.EnableInClassList("hidden", false);
    }

    public void SetStatus(string message)
    {
        if (!ready || message == null || message == lastStatus)
        {
            return;
        }

        lastStatus = message;
        statusLabel.text = message;
    }

    public void SetStreamer(bool visible, string message, float radius, float unloadRadius)
    {
        if (!ready || radiusRow == null)
        {
            return;
        }

        bool wasVisible = radiusRow.style.display != DisplayStyle.None;
        if (wasVisible != visible)
        {
            radiusRow.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            streamerLabel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        if (!visible)
        {
            return;
        }

        if (Mathf.Abs(radiusSlider.value - radius) > 0.5f)
        {
            radiusSlider.SetValueWithoutNotify(radius);
        }

        string radiusText = $"Load radius {radius:F0} m (unload {unloadRadius:F0} m)";
        if (radiusLabel.text != radiusText)
        {
            radiusLabel.text = radiusText;
        }

        if (streamerLabel.text != message)
        {
            streamerLabel.text = message;
        }
    }

    public void SetHint(string message)
    {
        if (!ready || message == null || message == lastHint)
        {
            return;
        }

        lastHint = message;
        hintLabel.text = message;
    }

    public void SetExporterWarning(bool visible)
    {
        if (!ready)
        {
            return;
        }

        exporterWarning.EnableInClassList("hidden", !visible);
    }

    public void SetBusy(bool busy)
    {
        if (!ready)
        {
            return;
        }

        levelList.SetEnabled(!busy);
        loadPathButton.SetEnabled(!busy);
        rescanButton.SetEnabled(!busy);
        clearFilterButton.SetEnabled(!busy);
    }

    public void SetFrameEnabled(bool enabled)
    {
        if (ready)
        {
            frameButton.SetEnabled(enabled);
        }
    }
}
}
