using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class TransparentPetFaceTrackingInstaller
{
    private const string UrpHostScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string IntegrationRootName = "TransparentPetIntegrationRoot";

    [MenuItem("Transparent Pet/Install Scene Face Tracking")]
    public static void InstallSceneFaceTracking()
    {
        Scene scene = EditorSceneManager.OpenScene(UrpHostScenePath, OpenSceneMode.Single);
        GameObject root = GameObject.Find(IntegrationRootName);
        if (root == null)
        {
            Debug.LogError("Transparent pet integration root not found: " + IntegrationRootName);
            return;
        }

        TransparentWindowController window = root.GetComponent<TransparentWindowController>();
        TransparentPetContextMenu contextMenu = root.GetComponent<TransparentPetContextMenu>();
        Camera camera = ResolveCamera(window);
        TransparentPetFreeCamera freeCamera = camera != null ? camera.GetComponent<TransparentPetFreeCamera>() : Object.FindAnyObjectByType<TransparentPetFreeCamera>();
        TransparentPetHeadLookAt headLookAt = Object.FindAnyObjectByType<TransparentPetHeadLookAt>();

        TransparentPetSceneFaceTracker tracker = root.GetComponent<TransparentPetSceneFaceTracker>();
        if (tracker == null)
        {
            tracker = root.AddComponent<TransparentPetSceneFaceTracker>();
        }

        tracker.windowController = window;
        tracker.freeCamera = freeCamera;
        tracker.headLookAt = headLookAt;
        tracker.targetCamera = camera;
        tracker.settingsKey = "ScenePet.FaceTracking.v3";
        tracker.trackingBackend = TransparentPetFaceTrackingBackend.ExternalMediaPipe;
        tracker.trackingEnabled = true;
        tracker.headFollowEnabled = true;
        tracker.cameraParallaxEnabled = true;
        tracker.cameraOrbitEnabled = true;
        tracker.mirrorHorizontal = true;
        tracker.mirrorVertical = true;
        tracker.launchExternalProcess = true;
        tracker.startCameraOnEnable = true;
        tracker.trackingAnchor = TransparentPetFaceTrackingAnchor.Head;
        tracker.cameraSightMode = TransparentPetCameraSightMode.ModelAxis;
        tracker.normalizedDeadZone = 0.07f;
        tracker.normalizedDepthDeadZone = 0.05f;
        tracker.offsetSmoothTime = 0.3f;
        tracker.depthSmoothTime = 0.32f;
        tracker.cameraTargetShiftMeters = 0.08f;
        tracker.cameraDepthShiftMeters = 0.06f;
        tracker.cameraHeightFollowMeters = 0.55f;
        tracker.cameraOrbitDeadZoneDegrees = 5f;
        tracker.cameraOrbitSmoothTime = 0.32f;
        tracker.cameraYawOrbitStrength = 1f;
        tracker.cameraPitchOrbitStrength = 0.35f;

        if (contextMenu != null)
        {
            contextMenu.sceneFaceTracker = tracker;
            EditorUtility.SetDirty(contextMenu);
        }

        EditorUtility.SetDirty(tracker);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("Scene face tracking installed on " + IntegrationRootName + " without rebuilding placement.");
    }

    private static Camera ResolveCamera(TransparentWindowController window)
    {
        if (window != null && window.transparentCamera != null)
        {
            return window.transparentCamera;
        }

        if (Camera.main != null)
        {
            return Camera.main;
        }

        return Object.FindAnyObjectByType<Camera>();
    }
}
