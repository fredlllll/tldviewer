using UnityEngine;

namespace TldLevels.Viewer
{

/// <summary>Creates the minimal runtime viewer environment.</summary>
public sealed class ViewerBootstrap : MonoBehaviour
{
    public const float FarClipPlane = 10000f;

    private void Awake()
    {
        Application.targetFrameRate = 120;

        var cameraObject = new GameObject("Viewer Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 30f, -60f), Quaternion.Euler(18f, 0f, 0f));
        var camera = cameraObject.AddComponent<Camera>();
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = FarClipPlane;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.09f, 0.12f, 0.16f);
        cameraObject.AddComponent<FlyCamera>();

        var lightObject = new GameObject("Sun");
        lightObject.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
        var sun = lightObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.2f;
        sun.color = new Color(0.92f, 0.96f, 1f);

        gameObject.AddComponent<LevelLoader>();
    }
}
}
