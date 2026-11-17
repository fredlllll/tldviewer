using UnityEngine;

namespace TldLevels.Viewer
{

/// <summary>
/// Completes the viewer scene: the camera and the sun already live in
/// <c>Viewer.unity</c> so the editor Game view shows the level before Play;
/// this only fills in anything missing, applies the shared render settings and
/// starts the level loader.
/// </summary>
public sealed class ViewerBootstrap : MonoBehaviour
{
    public const float FarClipPlane = 10000f;

    private static readonly Color BackgroundColor = new Color(0.09f, 0.12f, 0.16f);

    private void Awake()
    {
        Application.targetFrameRate = 120;

        Camera camera = Camera.main;
        if (camera == null)
        {
            var cameraObject = new GameObject("Viewer Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 30f, -60f), Quaternion.Euler(18f, 0f, 0f));
            camera = cameraObject.AddComponent<Camera>();
        }

        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = FarClipPlane;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = BackgroundColor;

        if (camera.GetComponent<FlyCamera>() == null)
        {
            camera.gameObject.AddComponent<FlyCamera>();
        }

        if (FindFirstObjectByType<Light>() == null)
        {
            var lightObject = new GameObject("Sun");
            lightObject.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            var sun = lightObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.color = new Color(0.92f, 0.96f, 1f);
        }

        gameObject.AddComponent<LevelLoader>();
    }
}
}
