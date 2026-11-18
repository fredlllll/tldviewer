using System;
using System.Collections.Generic;
using TldLevels.Viewer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Batch-mode validation for the UI Toolkit viewer front end: the scene's
/// UIDocument/panel settings wiring, the theme and the UXML element names that
/// <c>ViewerUi</c> binds. Run with:
/// <c>Unity.exe -batchmode -nographics -projectPath src/TldLevels.Viewer
/// -executeMethod ViewerUiSmokeTest.Run -logFile ui-smoke.log</c>
/// </summary>
public static class ViewerUiSmokeTest
{
    private const string ScenePath = "Assets/Scenes/Viewer.unity";

    private static readonly string[] RequiredElements =
    {
        "viewer-ui",
        "panel",
        "panel-body",
        "settings",
        "export-folder",
        "game-folder",
        "exporter-path",
        "exporter-warning",
        "rescan",
        "clear-filter",
        "filter",
        "level-list",
        "level-empty",
        "package-path",
        "load-path",
        "frame-level",
        "radius-row",
        "radius-label",
        "radius",
        "streamer-status",
        "status",
        "hint",
        "panel-toggle",
        "settings-toggle",
    };

    [MenuItem("Tools/TLD Levels/Smoke Test Viewer UI")]
    public static void RunMenu() => Run();

    public static void Run()
    {
        int exitCode = 1;
        try
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            Camera camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (camera == null)
            {
                throw new InvalidOperationException("Viewer scene has no camera; it must exist before Play.");
            }

            if (camera.GetComponent<FlyCamera>() == null)
            {
                throw new InvalidOperationException("Viewer camera has no FlyCamera component.");
            }

            if (Mathf.Abs(camera.farClipPlane - ViewerBootstrap.FarClipPlane) > 1f)
            {
                throw new InvalidOperationException($"Viewer camera far clip is {camera.farClipPlane}, expected 10000.");
            }

            Light sun = UnityEngine.Object.FindFirstObjectByType<Light>();
            if (sun == null || sun.type != LightType.Directional)
            {
                throw new InvalidOperationException("Viewer scene has no directional sun light.");
            }

            Quaternion sunRotation = sun.transform.rotation;
            float sunLength = Mathf.Sqrt(
                sunRotation.x * sunRotation.x + sunRotation.y * sunRotation.y +
                sunRotation.z * sunRotation.z + sunRotation.w * sunRotation.w);
            if (Mathf.Abs(sunLength - 1f) > 0.001f)
            {
                // A non-unit rotation makes the light direction non-normalized,
                // which trips Unity's IsNormalized(dir) assert every repaint.
                throw new InvalidOperationException(
                    $"Sun rotation is not normalized (length {sunLength:F4}).");
            }

            UIDocument document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            if (document == null)
            {
                throw new InvalidOperationException("Viewer scene has no UIDocument.");
            }

            if (document.panelSettings == null)
            {
                throw new InvalidOperationException("UIDocument has no PanelSettings (scene reference is broken).");
            }

            if (document.visualTreeAsset == null)
            {
                throw new InvalidOperationException("UIDocument has no visual tree asset (scene reference is broken).");
            }

            PanelSettings settings = document.panelSettings;
            ThemeStyleSheet theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(
                "Assets/UI/Resources/ViewerTheme.tss");
            if (theme == null)
            {
                throw new InvalidOperationException("ViewerTheme.tss did not import as a ThemeStyleSheet.");
            }

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(theme, out string themeGuid, out long themeLocalId))
            {
                Debug.Log($"[ui] theme {themeGuid} localFileId {themeLocalId}");
            }

            if (settings.themeStyleSheet == null)
            {
                throw new InvalidOperationException("PanelSettings has no theme style sheet.");
            }

            if (settings.scaleMode != PanelScaleMode.ConstantPixelSize)
            {
                throw new InvalidOperationException($"Unexpected panel scale mode: {settings.scaleMode}.");
            }

            var container = new VisualElement();
            document.visualTreeAsset.CloneTree(container);

            List<string> missing = new List<string>();
            foreach (string name in RequiredElements)
            {
                if (container.Q<VisualElement>(name) == null)
                {
                    missing.Add(name);
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Missing UXML elements: " + string.Join(", ", missing));
            }

            StyleSheet styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UI/Resources/ViewerUI.uss");
            if (styleSheet == null)
            {
                throw new InvalidOperationException("ViewerUI.uss failed to import as a StyleSheet.");
            }

            int referencedStyleSheets = 0;
            foreach (StyleSheet _ in document.visualTreeAsset.stylesheets)
            {
                referencedStyleSheets++;
            }

            if (referencedStyleSheets == 0)
            {
                throw new InvalidOperationException("ViewerUI.uxml does not reference ViewerUI.uss.");
            }

            Debug.Log($"[ui] {document.visualTreeAsset.name}: {RequiredElements.Length} elements bound, " +
                $"{referencedStyleSheets} style sheet(s), theme {theme.name}, " +
                $"reference {settings.referenceResolution}, scale mode {settings.scaleMode}");
            Debug.Log("[ui] PASS");
            exitCode = 0;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[ui] FAIL: {exception}");
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }
}
