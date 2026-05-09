using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class TransparentPetProductValidator
{
    private const string UrpHostScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string IntegrationRootName = "TransparentPetIntegrationRoot";
    private const int ExpectedControlPort = 17861;

    [MenuItem("Transparent Pet/Validate Product Scene")]
    public static void ValidateProductSceneFromMenu()
    {
        ValidationReport report = ValidateProductSceneInternal();
        if (report.ErrorCount == 0)
        {
            EditorUtility.DisplayDialog("Transparent Pet", report.Summary, "OK");
        }
    }

    public static void ValidateProductSceneBatch()
    {
        ValidationReport report = ValidateProductSceneInternal();
        if (report.ErrorCount > 0)
        {
            throw new InvalidOperationException(report.Summary);
        }
    }

    private static ValidationReport ValidateProductSceneInternal()
    {
        ValidationReport report = new ValidationReport();
        if (!File.Exists(ToProjectPath(UrpHostScenePath)))
        {
            report.Error("Product scene missing: " + UrpHostScenePath);
            report.Flush();
            return report;
        }

        Scene scene = EditorSceneManager.OpenScene(UrpHostScenePath, OpenSceneMode.Single);
        report.Require(scene.IsValid(), "Product scene opens successfully.");

        GameObject root = GameObject.Find(IntegrationRootName);
        if (root == null)
        {
            report.Error("Integration root missing: " + IntegrationRootName);
            report.Flush();
            return report;
        }

        ValidateBuildSettings(report);
        ValidateRuntimeFiles(report);
        ValidateSceneComponents(report, root);
        report.Flush();
        return report;
    }

    private static void ValidateBuildSettings(ValidationReport report)
    {
        bool sceneEnabled = false;
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            if (string.Equals(scenes[i].path, UrpHostScenePath, StringComparison.OrdinalIgnoreCase) && scenes[i].enabled)
            {
                sceneEnabled = true;
                break;
            }
        }

        report.Require(sceneEnabled, "Build Settings include the URP host scene.");
        report.WarnIf(EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64,
            "Active build target is not StandaloneWindows64.");
        report.WarnIf(!PlayerSettings.runInBackground, "PlayerSettings.runInBackground should stay enabled.");
        report.WarnIf(!PlayerSettings.resizableWindow, "Small-window mode needs PlayerSettings.resizableWindow enabled.");

        bool hasDirect3D11 = false;
        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        for (int i = 0; i < apis.Length; i++)
        {
            if (apis[i] == GraphicsDeviceType.Direct3D11)
            {
                hasDirect3D11 = true;
                break;
            }
        }

        report.Require(hasDirect3D11, "Windows graphics APIs include Direct3D11 for the current lilToon route.");
    }

    private static void ValidateRuntimeFiles(ValidationReport report)
    {
        RequireProjectFile(report, "Assets/StreamingAssets/GodotFinal/config/voice_routes.json");
        RequireProjectFile(report, "Assets/StreamingAssets/GodotFinal/scripts/run_volc_rtc_web_client.py");
        RequireProjectFile(report, "Assets/StreamingAssets/GodotFinal/src/pet_pose_bridge/unity_pose_client.py");
        RequireProjectFile(report, "Assets/StreamingAssets/GodotFinal/tools/volc_rtc_web/main.js");
        RequireProjectFile(report, "Assets/StreamingAssets/KawaiiUnity/official_actions.txt");

        WarnProjectFile(report, "Assets/StreamingAssets/GodotFinal/config/volc_traditional_voice_chat.local.json");
        WarnProjectFile(report, "Assets/StreamingAssets/GodotFinal/config/volc_traditional_companion_polling.local.json");

        string projectRoot = ProjectRoot;
        string workspaceRoot = Directory.GetParent(projectRoot) != null
            ? Directory.GetParent(projectRoot).FullName
            : projectRoot;
        string trackerPath = Path.Combine(workspaceRoot, "head_tracker", "head_tracker.py");
        report.Require(File.Exists(trackerPath), "External MediaPipe tracker exists outside Unity project.");
    }

    private static void ValidateSceneComponents(ValidationReport report, GameObject root)
    {
        TransparentWindowController window = RequireComponentInScope<TransparentWindowController>(report, root, "Window controller");
        TransparentPetContextMenu contextMenu = RequireComponentInScope<TransparentPetContextMenu>(report, root, "Right-click menu");
        TransparentPetRuntimeControls runtimeControls = RequireComponentInScope<TransparentPetRuntimeControls>(report, root, "Runtime controls");
        TransparentPetPlacementController placement = RequireComponentInScope<TransparentPetPlacementController>(report, root, "Placement controller");
        TransparentPetEnvironmentLightingBridge lighting = RequireComponentInScope<TransparentPetEnvironmentLightingBridge>(report, root, "Environment lighting bridge");
        TransparentPetSceneFaceTracker faceTracker = RequireComponentInScope<TransparentPetSceneFaceTracker>(report, root, "Scene face tracker");
        PetControlServer controlServer = RequireComponentInScope<PetControlServer>(report, root, "Pet control server");
        TransparentPetVoiceRuntimeLauncher voiceLauncher = RequireComponentInScope<TransparentPetVoiceRuntimeLauncher>(report, root, "Voice runtime launcher");
        TransparentPetKawaiiActionController actionController = RequireComponentInScope<TransparentPetKawaiiActionController>(report, root, "Kawaii action controller");
        PetStateController stateController = RequireComponentInScope<PetStateController>(report, root, "State controller");
        PetMouthController mouthController = RequireComponentInScope<PetMouthController>(report, root, "Mouth controller");
        SceneSubtitleController subtitles = root.GetComponentInChildren<SceneSubtitleController>(true);

        ValidateWindow(report, window);
        ValidateContextMenu(report, contextMenu, voiceLauncher, actionController, faceTracker, placement, runtimeControls);
        ValidateCamera(report, window, contextMenu);
        ValidatePlacementPersistence(report, placement);
        ValidateLighting(report, lighting);
        ValidateVoice(report, voiceLauncher, controlServer, faceTracker);
        ValidateActions(report, actionController, stateController);
        ValidateMouthAndSubtitles(report, mouthController, stateController, subtitles);
        ValidateFaceTracker(report, faceTracker, window);
        ValidateRenderers(report, root);
    }

    private static void ValidateWindow(ValidationReport report, TransparentWindowController window)
    {
        if (window == null)
        {
            return;
        }

        report.Require(window.route == TransparentPetRoute.SceneHost, "Window route is SceneHost.");
        report.Require(window.transparentCamera != null, "Window has a target camera.");
        report.Require(window.hitRoot != null, "Window hit root is assigned.");
        if (window.route == TransparentPetRoute.SceneHost)
        {
            report.Require(!window.clickThroughOutsideHit, "Scene host does not use desktop click-through hit routing.");
        }
        else
        {
            report.Require(window.clickThroughOutsideHit, "Desktop window click-through outside model is enabled.");
        }
        report.Require(window.persistRuntimeWindowSettings, "Runtime window settings are persisted.");
        report.Require(window.primaryRightWindowSizePixels.x > 0 && window.primaryRightWindowSizePixels.y > 0,
            "Small-window default size is configured.");
        report.WarnIf(window.configureNativeWindow && !window.alwaysOnTop,
            "Native window is configured but always-on-top is disabled.");
    }

    private static void ValidateContextMenu(
        ValidationReport report,
        TransparentPetContextMenu contextMenu,
        TransparentPetVoiceRuntimeLauncher voiceLauncher,
        TransparentPetKawaiiActionController actionController,
        TransparentPetSceneFaceTracker faceTracker,
        TransparentPetPlacementController placement,
        TransparentPetRuntimeControls runtimeControls)
    {
        if (contextMenu == null)
        {
            return;
        }

        report.Require(contextMenu.route == TransparentPetRoute.SceneHost, "Context menu route is SceneHost.");
        report.Require(contextMenu.voiceLauncher == voiceLauncher, "Context menu is wired to voice launcher.");
        report.Require(contextMenu.actionController == actionController, "Context menu is wired to action controller.");
        report.Require(contextMenu.sceneFaceTracker == faceTracker, "Context menu is wired to face tracker.");
        report.Require(contextMenu.placementController == placement, "Context menu is wired to placement controller.");
        report.Require(contextMenu.runtimeControls == runtimeControls, "Context menu is wired to runtime controls.");
        report.Require(contextMenu.panelSize.x >= 460f && contextMenu.panelSize.y >= 540f,
            "Context menu panel is large enough for product controls.");
    }

    private static void ValidateCamera(ValidationReport report, TransparentWindowController window, TransparentPetContextMenu contextMenu)
    {
        Camera camera = window != null && window.transparentCamera != null
            ? window.transparentCamera
            : UnityEngine.Object.FindAnyObjectByType<Camera>();
        report.Require(camera != null, "Product camera exists.");
        if (camera == null)
        {
            return;
        }

        TransparentPetFreeCamera freeCamera = camera.GetComponent<TransparentPetFreeCamera>();
        report.Require(freeCamera != null, "Product camera has TransparentPetFreeCamera.");
        if (freeCamera == null)
        {
            return;
        }

        report.Require(freeCamera.windowController == window, "Free camera is wired to window controller.");
        report.Require(freeCamera.contextMenu == contextMenu, "Free camera is wired to context menu.");
        report.Require(freeCamera.keyboardMouseControls, "Keyboard and mouse camera controls are enabled.");
        report.Require(freeCamera.requirePetHitForInput, "Camera drag input is scoped to the pet hit area.");
        report.Require(freeCamera.depthOfFieldEnabled, "Depth of field is enabled by default.");
        report.Require(freeCamera.lockDepthOfFieldToPet, "Depth of field is locked to the pet.");
        report.Require(freeCamera.persistCameraState, "Free camera runtime state is persisted.");
        report.Require(!freeCamera.useSavedCameraInEditor, "Editor play does not load user camera saves.");
        report.Require(!freeCamera.saveCameraInEditor, "Editor play does not overwrite user camera saves.");
        report.Require(!string.IsNullOrWhiteSpace(freeCamera.cameraSaveKey) &&
            freeCamera.cameraSaveKey.StartsWith("ScenePet.", StringComparison.Ordinal),
            "Free camera uses the scene product PlayerPrefs namespace.");
        report.WarnIf(freeCamera.distance > 1.2f, "Free camera distance is unusually large; F may frame the pet poorly.");
    }

    private static void ValidatePlacementPersistence(ValidationReport report, TransparentPetPlacementController placement)
    {
        if (placement == null)
        {
            return;
        }

        report.Require(placement.persistRuntimePlacement, "Runtime placement state is persisted.");
        report.Require(placement.lockCameraTargetToPet, "Camera focus stays locked to the pet while placement changes.");
        report.Require(!placement.useSavedPlacementInEditor, "Editor play does not load user placement saves.");
        report.Require(!placement.savePlacementInEditor, "Editor play does not overwrite user placement saves.");
        report.Require(!string.IsNullOrWhiteSpace(placement.placementSaveKey) &&
            placement.placementSaveKey.StartsWith("ScenePet.", StringComparison.Ordinal),
            "Placement uses the scene product PlayerPrefs namespace.");
    }

    private static void ValidateLighting(ValidationReport report, TransparentPetEnvironmentLightingBridge lighting)
    {
        if (lighting == null)
        {
            return;
        }

        report.Require(lighting.targetRoot != null, "Lighting bridge target root is assigned.");
        report.Require(lighting.probeAnchor != null, "Lighting bridge probe anchor is assigned.");
        report.Require(lighting.tuneLilToonMaterials, "Lighting bridge tunes lilToon materials.");
        report.Require(lighting.receiveSceneShadows, "Pet receives scene shadows.");
        report.Require(lighting.refreshLightingDuringPlay, "Lighting bridge refreshes during Play.");
        report.Require(lighting.applyAfterPlayStart, "Lighting bridge reapplies after Play startup.");
    }

    private static void ValidateVoice(
        ValidationReport report,
        TransparentPetVoiceRuntimeLauncher voiceLauncher,
        PetControlServer controlServer,
        TransparentPetSceneFaceTracker faceTracker)
    {
        if (voiceLauncher == null || controlServer == null)
        {
            return;
        }

        report.Require(controlServer.port == ExpectedControlPort, "Pet control server uses port " + ExpectedControlPort + ".");
        report.Require(controlServer.startOnPlay, "Pet control server starts on Play.");
        report.WarnIf(controlServer.voiceLauncher != null && controlServer.voiceLauncher != voiceLauncher,
            "Pet control server references a different voice launcher.");
        report.Require(voiceLauncher.presentationRoute == "unity", "Voice presentation route is unity.");
        report.Require(voiceLauncher.presentationPort == controlServer.port, "Voice launcher targets the Unity control port.");
        report.WarnIf(voiceLauncher.startOnPlay, "Voice startOnPlay flag is true; allowStartOnPlayInProduct still gates real startup.");
        report.Require(!voiceLauncher.allowStartOnPlayInProduct, "Product auto voice start remains disabled.");
        report.Require(voiceLauncher.monitorVoiceHealth, "Voice health monitor is enabled.");
        report.Require(voiceLauncher.sceneFaceTracker == faceTracker, "Voice launcher references the scene face tracker.");
        report.Require(voiceLauncher.screenVisionWidth >= 1280 && voiceLauncher.screenVisionHeight >= 720,
            "Screen vision target is at least 720p.");
        report.Require(voiceLauncher.cameraVideoWidth >= 640 && voiceLauncher.cameraVideoHeight >= 480,
            "Camera video stream is at least 480p.");
    }

    private static void ValidateActions(
        ValidationReport report,
        TransparentPetKawaiiActionController actionController,
        PetStateController stateController)
    {
        if (actionController == null || stateController == null)
        {
            return;
        }

        report.Require(actionController.useAnimatorController, "Kawaii action controller uses Animator Controller.");
        report.Require(actionController.randomAutoSwitch, "Random idle action switching is enabled.");
        report.Require(actionController.useProductRandomActionWhitelist, "Random idle actions use the product whitelist.");
        report.Require(Mathf.Abs(actionController.randomActionIntervalSeconds - 8f) <= 0.2f,
            "Random idle action interval is 8 seconds.");
        report.Require(actionController.transitionSeconds > 0.05f, "Action transitions are smoothed.");
        report.Require(actionController.modelRoot != null, "Action controller has model root.");
        report.Require(string.Equals(actionController.defaultActionName, "KA_Idle01_breathing", StringComparison.OrdinalIgnoreCase),
            "Default action is breathing idle.");
        report.Require(stateController.actionController == actionController, "State controller is wired to action controller.");
        report.Require(stateController.pauseRandomActionsDuringVoice, "Voice states pause random idle actions.");
        report.Require(stateController.speakingActionPool != null && stateController.speakingActionPool.Count >= 3,
            "Speaking action pool has multiple actions.");
        ValidateRandomActionWhitelist(report, actionController);

        Animator animator = actionController.GetComponentInChildren<Animator>(true);
        report.Require(animator != null, "Animator exists under action controller.");
        if (animator != null)
        {
            report.Require(animator.runtimeAnimatorController != null, "Animator has a runtime controller.");
            report.Require(!animator.applyRootMotion, "Animator root motion is disabled.");
        }
    }

    private static void ValidateRandomActionWhitelist(ValidationReport report, TransparentPetKawaiiActionController actionController)
    {
        string[] safeActions =
        {
            "KA_Idle02_LookLeftAndRight",
            "KA_Idle08_ComeUpWithAnIdea",
            "KA_Idle16_WaveHands",
            "KA_Idle28_Laugh",
            "KA_Idle45_WaveHandSlightly",
            "KA_Idle50_StandingTalk1_1"
        };
        for (int i = 0; i < safeActions.Length; i++)
        {
            report.Require(actionController.IsRandomActionAllowedForProduct(safeActions[i]),
                "Random whitelist allows safe action " + safeActions[i] + ".");
        }

        string[] riskyActions =
        {
            "KA_Idle17_StumbleAndFall",
            "KA_Idle54_CartwheelAndBackHandspring",
            "KA_Idle55_Backflip",
            "KA_Idle56_Handstand",
            "KA_Idle57_Dance05",
            "KA_Idle58_Dance06"
        };
        for (int i = 0; i < riskyActions.Length; i++)
        {
            report.Require(!actionController.IsRandomActionAllowedForProduct(riskyActions[i]),
                "Random whitelist blocks risky action " + riskyActions[i] + ".");
        }
    }

    private static void ValidateMouthAndSubtitles(
        ValidationReport report,
        PetMouthController mouthController,
        PetStateController stateController,
        SceneSubtitleController subtitles)
    {
        if (mouthController == null || stateController == null)
        {
            return;
        }

        report.Require(mouthController.mouthFlapEnabled, "Mouth flap is enabled.");
        report.Require(mouthController.audioMouthUseVolumeVisemes, "Audio mouth uses varied volume visemes.");
        report.WarnIf(mouthController.smoothSpeed > 30f, "Mouth smooth speed is high and may look jittery.");
        report.Require(stateController.mouthController == mouthController, "State controller is wired to mouth controller.");
        report.Require(stateController.enableSceneScreenSubtitles, "Scene subtitles are enabled.");
        report.Require(stateController.screenSubtitleController != null || subtitles != null, "Scene subtitle controller exists.");
    }

    private static void ValidateFaceTracker(
        ValidationReport report,
        TransparentPetSceneFaceTracker faceTracker,
        TransparentWindowController window)
    {
        if (faceTracker == null)
        {
            return;
        }

        report.Require(faceTracker.windowController == window, "Face tracker is wired to window controller.");
        report.Require(faceTracker.trackingBackend == TransparentPetFaceTrackingBackend.ExternalMediaPipe,
            "Face tracker defaults to External MediaPipe.");
        report.Require(faceTracker.trackingEnabled, "Face tracking is enabled.");
        report.Require(faceTracker.startCameraOnEnable, "Standalone face tracking starts with the scene.");
        report.Require(faceTracker.launchExternalProcess, "Face tracker launches the external process.");
        report.Require(faceTracker.trackingAnchor == TransparentPetFaceTrackingAnchor.Head,
            "Face tracker follows the head by default.");
        report.Require(faceTracker.cameraSightMode == TransparentPetCameraSightMode.ModelAxis,
            "Face-tracking camera sight uses the model axis.");
        report.Require(faceTracker.requestedWidth >= 640 && faceTracker.requestedHeight >= 480,
            "Face tracker camera request is at least 480p.");
        report.Require(faceTracker.externalFrameServerEnabled, "Face tracker frame server is enabled for virtual camera sharing.");
    }

    private static void ValidateRenderers(ValidationReport report, GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        report.Require(renderers.Length > 0, "Pet renderers exist under integration root.");

        int skinnedCount = 0;
        int meshRendererCount = 0;
        int toonMaterialCount = 0;
        int missingMaterialCount = 0;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] is SkinnedMeshRenderer)
            {
                skinnedCount++;
            }
            else if (renderers[i] is MeshRenderer)
            {
                meshRendererCount++;
            }

            Material[] materials = renderers[i].sharedMaterials;
            for (int j = 0; j < materials.Length; j++)
            {
                Material material = materials[j];
                if (material == null)
                {
                    missingMaterialCount++;
                    continue;
                }

                if (material.shader != null &&
                    (material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     material.shader.name.IndexOf("DesktopPet/", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    toonMaterialCount++;
                }
            }
        }

        report.Require(skinnedCount + meshRendererCount > 0, "Pet has visible mesh renderers.");
        report.WarnIf(skinnedCount == 0, "Public source build is using the basic placeholder model; import a humanoid model for full character animation.");
        report.Require(toonMaterialCount > 0, "Pet has toon-compatible materials assigned.");
        report.WarnIf(missingMaterialCount > 0, "Pet has " + missingMaterialCount + " missing material slot(s).");
    }

    private static T RequireComponentInScope<T>(ValidationReport report, GameObject root, string label) where T : Component
    {
        T component = root.GetComponent<T>();
        if (component == null)
        {
            component = root.GetComponentInChildren<T>(true);
        }

        report.Require(component != null, label + " is present.");
        return component;
    }

    private static void RequireProjectFile(ValidationReport report, string relativePath)
    {
        report.Require(File.Exists(ToProjectPath(relativePath)), "Required runtime file exists: " + relativePath);
    }

    private static void WarnProjectFile(ValidationReport report, string relativePath)
    {
        report.WarnIf(!File.Exists(ToProjectPath(relativePath)), "Local runtime config missing: " + relativePath);
    }

    private static string ToProjectPath(string relativePath)
    {
        return Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ProjectRoot
    {
        get
        {
            DirectoryInfo assets = Directory.GetParent(Application.dataPath);
            return assets != null ? assets.FullName : Directory.GetCurrentDirectory();
        }
    }

    private sealed class ValidationReport
    {
        private readonly List<string> _messages = new List<string>();

        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }
        public string Summary => "Transparent pet product validation: " +
            ErrorCount + " error(s), " +
            WarningCount + " warning(s).";

        public void Require(bool condition, string message)
        {
            if (condition)
            {
                Info("OK: " + message);
            }
            else
            {
                Error(message);
            }
        }

        public void WarnIf(bool condition, string message)
        {
            if (condition)
            {
                Warning(message);
            }
            else
            {
                Info("OK: " + message);
            }
        }

        public void Error(string message)
        {
            ErrorCount++;
            _messages.Add("[ERROR] " + message);
        }

        private void Warning(string message)
        {
            WarningCount++;
            _messages.Add("[WARN] " + message);
        }

        private void Info(string message)
        {
            _messages.Add("[INFO] " + message);
        }

        public void Flush()
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                string message = "[TransparentPetProductValidator] " + _messages[i];
                if (_messages[i].StartsWith("[ERROR]", StringComparison.Ordinal))
                {
                    Debug.LogError(message);
                }
                else if (_messages[i].StartsWith("[WARN]", StringComparison.Ordinal))
                {
                    Debug.LogWarning(message);
                }
                else
                {
                    Debug.Log(message);
                }
            }

            if (ErrorCount > 0)
            {
                Debug.LogError("[TransparentPetProductValidator] " + Summary);
            }
            else if (WarningCount > 0)
            {
                Debug.LogWarning("[TransparentPetProductValidator] " + Summary);
            }
            else
            {
                Debug.Log("[TransparentPetProductValidator] " + Summary);
            }
        }
    }
}
