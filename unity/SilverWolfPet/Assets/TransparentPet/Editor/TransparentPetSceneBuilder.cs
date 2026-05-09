using System.IO;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class TransparentPetSceneBuilder
{
    private const string ScenePath = "Assets/TransparentPet/Scenes/TransparentWindowScene.unity";
    private const string UrpHostScenePath = "Assets/Scenes/BlenderIndoorScene.unity";
    private const string BuildPath = "Builds/TransparentWindowPet/TransparentWindowPet.exe";
    private const string UrpHostBuildPath = "Builds/ScenePet/ScenePet.exe";
    private const string PlacementOriginName = "TransparentPetPlacementOrigin";
    private const string IntegrationRootName = "TransparentPetIntegrationRoot";
    private const string ModelInstanceName = "PetModelRoot";
    private const string TextureFolder = "Assets/TransparentPet/Textures";
    private const string GeneratedMaterialFolder = "Assets/TransparentPet/Generated/Materials";
    private const string GeneratedControllerFolder = "Assets/TransparentPet/Generated/Controllers";
    private const string PublicPlaceholderMaterialPath = GeneratedMaterialFolder + "/PublicPlaceholder_Desktop.mat";
    private const string PublicPlaceholderDarkMaterialPath = GeneratedMaterialFolder + "/PublicPlaceholderDark_Desktop.mat";
    private const string KawaiiAnimationFolder = "Assets/KAWAII_ANIMATIOMS_100/Assets/Animations";
    private const string KawaiiGenericAnimationFolder = "Assets/KAWAII_ANIMATIOMS_100/Assets/Generic/Animations";
    private const string CopiedKawaiiAnimationFolder = "Assets/TransparentPet/OfficialKawaii/Animations";
    private const string OfficialActionsManifestPath = "Assets/StreamingAssets/KawaiiUnity/official_actions.txt";
    private const string KawaiiAnimatorControllerPath = GeneratedControllerFolder + "/TransparentPetKawaiiActions.controller";
    private const int LookDevSlotCount = 45;
    private static readonly bool UseUnityToonShaderTrial = false;

    private static readonly string[] LookDevMaterialGuids =
    {
        "10ec98b1a25740119b4762544a48102e",
        "168dad8f8285469ea63bf062840c86e3",
        "6675178a5e5f468d99303cde4d4c7808",
        "ddd65491a2e64af88a11ac7f5bfcce6b",
        "754f72991a15465da433bdc5851291ec",
        "dada92595ac44804824546408b74b7e4",
        "fac28f992e0a43619624acfec453521a",
        "6ba31fd53473477d94b3796ed1d94dc8",
        "d3db44f995264e21a4eaf1e8b1b49de5",
        "b13a5ffc517c4da789b2ab94ef5d3645",
        "b8b0fbc38f1a4b859844981a2f4566d5",
        "d1a20d2f699f4b129452ee89bfd10e13",
        "92df1cb275ad4480820f8e0eca7d8a27",
        "1011129e82cd441087a3c5cedbcf4467",
        "e5d59f3c36fa49d3b558900ed6808c00",
        "bdc4231f7ec841359eaf9de40241c564",
        "56ea159dd0784223b06aa605c9159385",
        "21b09c977d1f446390fcdc70899c9330",
        "2802ee3bdf5749afa6b7f97299e49374",
        "250c9466016a4c2ba0f5ef3be4dc1e7b",
        "80f192f7da104520af6dacc6744664df",
        "40af91204ba141aeb394ac75340f600c",
        "eddd3add3fdc46a68e2c2c7510ab3a41",
        "4d8daf014b594f3ca35a53b13ae79bfd",
        "59a7b22fff7940e0959cc5735d92708b",
        "8da596001c9449a2b156e2228a23e3c9",
        "87311f5b5c604ee7a021880464d5ca9a",
        "8d320cb95f764e858502cade16f18a95",
        "c1069c7640aa41bdbddb03c8dddeeac5",
        "23ba5c1b8a3b467aa4a25b694df35a3c",
        "094ffaa3edd34e69bf11b8656425855a",
        "1f048334fa7742e2a61f5d4fbef179f2",
        "48f677d11edd4703843e4eb4ecfd4d81",
        "a178e6a38cab43909bfae7a3f50394b3",
        "74eec813d85b40b681a92baf4b0453bd",
        "e9a7a63128414584a2c036ad362d154a",
        "05214a345a64479c9ea94c3739205e7f",
        "56e6be76daf440f693de94ab72cd58fb",
        "7e97150e58a64ae18981dfcfcb3567ca",
        "c7d269166d124392a0b993542d2b0a69",
        "1791b035394c4bb096d4af2e665f4de1",
        "ce1c75f4fedf4cc9b2df28035d586adf",
        "9cc5f906021043198d16e4fe58d666a6",
        "1b69175f04e843d88aa3f303fa677d6e",
        "794f29b629114b9c89fe74fad0c63361"
    };

    private struct MaterialSourceData
    {
        public Texture MainTexture;
        public Color Tint;
        public Color ShadowColor;
        public float Alpha;
    }

    private struct SavedPlacement
    {
        public bool HasOrigin;
        public Vector3 OriginPosition;
        public Quaternion OriginRotation;
        public Vector3 OriginScale;
        public bool HasRoot;
        public Vector3 RootLocalPosition;
        public Quaternion RootLocalRotation;
        public Vector3 RootLocalScale;
        public bool HasModelLocal;
        public Vector3 ModelLocalPosition;
        public Quaternion ModelLocalRotation;
        public Vector3 ModelLocalScale;
    }

    [MenuItem("Transparent Pet/Rebuild Transparent Window Scene")]
    public static void RebuildScene()
    {
        Directory.CreateDirectory("Assets/TransparentPet/Scenes");
        Directory.CreateDirectory(GeneratedMaterialFolder);
        Directory.CreateDirectory(GeneratedControllerFolder);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ConfigureRenderSettings();
        QualitySettings.antiAliasing = 8;

        GameObject root = new GameObject("TransparentWindowRoot");
        root.transform.position = Vector3.zero;

        Camera camera = CreateCamera();
        Light light = CreateLight();
        GameObject petBody = CreatePetBody();
        Animator petAnimator = ConfigureIdleAnimator(petBody);
        petBody.transform.SetParent(root.transform, true);

        TransparentWindowController window = root.AddComponent<TransparentWindowController>();
        window.transparentCamera = camera;
        window.hitRoot = petBody.transform;
        window.route = TransparentPetRoute.DesktopTransparent;
        window.alwaysOnTop = true;
        window.clickThroughOutsideHit = true;
        window.moveToSecondaryMonitorOnStart = true;
        window.preferredMonitorIndex = 0;
        window.resizeToTargetMonitorWorkArea = false;
        window.useFullMonitorBounds = false;
        window.compensateMonitorDpiScale = true;
        window.presentationMode = TransparentWindowController.MonitorPresentationMode.SmallWindow;
        window.orientationMode = TransparentWindowController.MonitorOrientationMode.FollowMonitor;
        window.monitorNormalizedPosition = new Vector2(1f, 0.5f);
        window.primaryRightWindowSizePixels = new Vector2Int(720, 960);
        window.primaryRightWindowPaddingPixels = new Vector2Int(24, 48);
        window.monitorPaddingPixels = window.primaryRightWindowPaddingPixels;
        window.windowSettingsKey = "DesktopPet.Window.v1";

        TransparentPetPerformanceController performanceController = root.AddComponent<TransparentPetPerformanceController>();
        performanceController.settingsKey = "DesktopPet.Performance.v1";
        performanceController.limitFrameRate = true;
        performanceController.targetFrameRate = 60;
        performanceController.verticalSync = false;
        performanceController.msaaSamples = 4;
        performanceController.overrideRenderScale = false;

        WindowDragHandle drag = root.AddComponent<WindowDragHandle>();
        drag.windowController = window;

        TransparentPetKawaiiActionController actionController = petBody.AddComponent<TransparentPetKawaiiActionController>();
        actionController.modelRoot = petBody.transform;
        actionController.actionManifestFile = "KawaiiUnity/official_actions.txt";
        actionController.actionBundleDirectory = "GodotFinal/assets/action_import/kawaii100";
        actionController.defaultActionName = "KA_Idle01_breathing";
        actionController.idleActionName = "KA_Idle01_breathing";
        actionController.onlyIdleActions = true;
        actionController.autoPlay = true;
        actionController.randomAutoSwitch = true;
        actionController.useProductRandomActionWhitelist = true;
        actionController.randomActionIntervalSeconds = 8f;
        // Legacy pose/state and JSON bone routes are deprecated; runtime actions use Unity Humanoid clips.
        actionController.useAnimatorController = true;
        actionController.menuOnly = false;
        actionController.applyBundleBoneRotations = false;
        actionController.applyHandFingerRotations = false;
        actionController.transitionSeconds = 0.62f;

        PetExpressionController expressionController = petBody.AddComponent<PetExpressionController>();
        expressionController.scanRoot = petBody.transform;
        expressionController.expressionMapPath = "GodotFinal/config/expression_map.json";
        expressionController.logBlendShapeReport = true;

        PetBlinkController blinkController = petBody.AddComponent<PetBlinkController>();
        blinkController.scanRoot = petBody.transform;
        blinkController.expressionController = expressionController;

        PetMouthController mouthController = petBody.AddComponent<PetMouthController>();
        mouthController.expressionController = expressionController;
        mouthController.driveFromAudio = false;
        mouthController.smoothSpeed = 22f;
        mouthController.externalHoldSeconds = 0.38f;
        mouthController.mouthFlapOpenWeight = 0.72f;
        mouthController.audioMouthTimeoutSeconds = 0.75f;
        mouthController.minAudioMouthOpen = 0.08f;
        mouthController.audioMouthUseVolumeVisemes = true;
        mouthController.audioMouthClosedThreshold = 0.035f;
        mouthController.audioMouthPeakThreshold = 0.82f;
        mouthController.audioMouthHoldMinSeconds = 0.1f;
        mouthController.audioMouthHoldMaxSeconds = 0.2f;

        PetBubbleController bubbleController = petBody.AddComponent<PetBubbleController>();
        bubbleController.searchRoot = petBody.transform;
        bubbleController.characterAnimator = petAnimator;
        bubbleController.worldCamera = camera;
        bubbleController.worldOffset = new Vector3(0f, 0.5f, 0f);
        bubbleController.defaultVisibleSeconds = 4.2f;
        bubbleController.maxVisibleMessages = 3;
        bubbleController.bubbleSize = new Vector2(210f, 48f);
        bubbleController.minBubbleWidth = 54f;
        bubbleController.minBubbleHeight = 26f;
        bubbleController.horizontalPadding = 10f;
        bubbleController.verticalPadding = 5f;
        bubbleController.bubbleSpacing = 5f;
        bubbleController.fontSize = 15;
        bubbleController.maxMessageCharacters = 96;
        bubbleController.mergeWindowSeconds = 1.4f;
        bubbleController.afterSpeechVisibleSeconds = 1.0f;
        bubbleController.canvasScale = 0.0039f;

        PetStateController stateController = petBody.AddComponent<PetStateController>();
        stateController.animator = petAnimator;
        stateController.actionController = actionController;
        stateController.expressionController = expressionController;
        stateController.mouthController = mouthController;
        stateController.bubbleController = bubbleController;
        stateController.enableSceneScreenSubtitles = false;
        stateController.initialState = "idle";
        stateController.idleActionName = "KA_Idle01_breathing";
        stateController.speakingActionName = "KA_Idle50_StandingTalk1_1";
        stateController.speakingActionPool = CreateSpeakingActionPool();
        stateController.thinkingActionName = "KA_Idle08_ComeUpWithAnIdea";
        stateController.listeningActionName = "KA_Idle02_LookLeftAndRight";
        stateController.happyActionName = "KA_Idle28_Laugh";
        stateController.angryActionName = "KA_Idle27_Angry";
        stateController.surprisedActionName = "KA_Idle29_Surprised";
        stateController.sleepyActionName = "KA_Idle09_Waiting";
        stateController.queueBubbleTextForMouth = true;
        stateController.pauseRandomActionsDuringVoice = true;

        TransparentPetSkeletonHitMask skeletonHitMask = petBody.AddComponent<TransparentPetSkeletonHitMask>();
        skeletonHitMask.animator = petAnimator != null ? petAnimator : petBody.GetComponentInChildren<Animator>();
        skeletonHitMask.targetCamera = camera;
        skeletonHitMask.bodyRadiusPixels = 18f;
        skeletonHitMask.headRadiusPixels = 22f;
        skeletonHitMask.limbRadiusPixels = 8f;
        skeletonHitMask.handFootRadiusPixels = 7f;
        window.skeletonHitMask = skeletonHitMask;

        TransparentPetRuntimeControls runtimeControls = petBody.AddComponent<TransparentPetRuntimeControls>();
        runtimeControls.modelRoot = petBody.transform;
        runtimeControls.skeletonHitMask = skeletonHitMask;
        runtimeControls.keyLight = light;
        runtimeControls.currentRenderPreset = "DesktopPetSoft";
        runtimeControls.lightMode = 0;
        runtimeControls.defaultForm = TransparentPetRuntimeControls.PetForm.Base;
        runtimeControls.defaultGlassesVisible = true;
        runtimeControls.defaultWingsVisible = false;
        runtimeControls.ApplyConfiguredDefaults();
        EditorUtility.SetDirty(runtimeControls);

        TransparentPetHeadLookAt headLookAt = ConfigureHeadLookAt(petAnimator, petBody, camera);
        headLookAt.settingsKey = "DesktopPet.HeadLookAt.v1";

        TransparentPetFreeCamera freeCamera = camera.gameObject.AddComponent<TransparentPetFreeCamera>();
        freeCamera.windowController = window;
        freeCamera.targetCamera = camera;
        freeCamera.enabledInput = true;
        freeCamera.target = new Vector3(0f, 0.75f, 0f);
        freeCamera.distance = 0.31f;
        freeCamera.requirePetHitForInput = true;
        freeCamera.freeSceneInput = false;
        freeCamera.depthOfFieldEnabled = true;
        freeCamera.lockDepthOfFieldToPet = true;
        freeCamera.cameraSaveKey = "DesktopPet.FreeCamera.v1";

        TransparentPetPlacementController placementController = root.AddComponent<TransparentPetPlacementController>();
        placementController.freeCamera = freeCamera;
        placementController.cameraTargetLocalOffset = new Vector3(0f, 0.75f, 0f);
        placementController.lockCameraTargetToPet = true;
        placementController.placementSaveKey = "DesktopPet.Placement.v1";
        placementController.CaptureCurrentTransform();

        TransparentPetWorkshopManager workshopManager = root.AddComponent<TransparentPetWorkshopManager>();
        workshopManager.modelRoot = petBody.transform;
        workshopManager.runtimeControls = runtimeControls;
        workshopManager.actionController = actionController;
        workshopManager.headLookAt = headLookAt;
        workshopManager.skeletonHitMask = skeletonHitMask;
        workshopManager.expressionController = expressionController;
        workshopManager.blinkController = blinkController;
        workshopManager.windowController = window;
        workshopManager.targetCamera = camera;

        TransparentPetContextMenu contextMenu = root.AddComponent<TransparentPetContextMenu>();
        contextMenu.windowController = window;
        contextMenu.route = TransparentPetRoute.DesktopTransparent;
        contextMenu.freeCamera = freeCamera;
        contextMenu.runtimeControls = runtimeControls;
        contextMenu.actionController = actionController;
        contextMenu.placementController = placementController;
        contextMenu.headLookAt = headLookAt;
        contextMenu.performanceController = performanceController;
        contextMenu.workshopManager = workshopManager;
        contextMenu.panelSize = new Vector2(520f, 560f);
        freeCamera.contextMenu = contextMenu;
        performanceController.targetCameras = new[] { camera };

        PetControlServer petControlServer = root.AddComponent<PetControlServer>();
        petControlServer.stateController = stateController;
        petControlServer.host = "127.0.0.1";
        petControlServer.port = 17861;
        petControlServer.startOnPlay = true;
        petControlServer.logCommands = false;

        TransparentPetVoiceRuntimeLauncher voiceLauncher = root.AddComponent<TransparentPetVoiceRuntimeLauncher>();
        voiceLauncher.streamingRootRelativePath = "GodotFinal";
        voiceLauncher.presentationRoute = "unity";
        voiceLauncher.presentationHost = "127.0.0.1";
        voiceLauncher.presentationPort = petControlServer.port;
        voiceLauncher.selectedRouteId = "traditional_vision";
        voiceLauncher.companionPollingIntervalSec = 8;
        voiceLauncher.realtimeMonitoringIntervalSec = 8;
        voiceLauncher.startOnPlay = false;
        voiceLauncher.allowStartOnPlayInProduct = false;
        contextMenu.voiceLauncher = voiceLauncher;

        camera.transform.SetParent(root.transform, true);
        light.transform.SetParent(root.transform, true);

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

        PlayerSettings.companyName = "LocalDesktopPet";
        PlayerSettings.productName = "TransparentWindowPet";
        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultScreenWidth = 720;
        PlayerSettings.defaultScreenHeight = 960;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.forceSingleInstance = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.useFlipModelSwapchain = false;
        QualitySettings.antiAliasing = 8;

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Transparent window scene rebuilt: " + ScenePath);
    }

    public static void BuildWindows()
    {
        RebuildScene();
        Directory.CreateDirectory(Path.GetDirectoryName(BuildPath));
        BuildPipeline.BuildPlayer(
            new[] { ScenePath },
            BuildPath,
            BuildTarget.StandaloneWindows64,
            BuildOptions.None);
        Debug.Log("Transparent window build written: " + Path.GetFullPath(BuildPath));
    }

    [MenuItem("Transparent Pet/Build Scene Host Windows")]
    public static void BuildSceneHostWindows()
    {
        IntegrateIntoUrpHostScene();
        Directory.CreateDirectory(Path.GetDirectoryName(UrpHostBuildPath));
        BuildPipeline.BuildPlayer(
            new[] { UrpHostScenePath },
            UrpHostBuildPath,
            BuildTarget.StandaloneWindows64,
            BuildOptions.None);
        Debug.Log("Scene host build written: " + Path.GetFullPath(UrpHostBuildPath));
    }

    private static List<string> CreateSpeakingActionPool()
    {
        return new List<string>
        {
            "KA_Idle50_StandingTalk1_1",
            "KA_Idle51_StandingTalk1_2",
            "KA_Idle12_LeaningForward",
            "KA_Idle16_WaveHands",
            "KA_Idle43_HandOnHip",
            "KA_Idle45_WaveHandSlightly"
        };
    }

    [MenuItem("Transparent Pet/Integrate Into URP Host Scene")]
    public static void IntegrateIntoUrpHostScene()
    {
        Directory.CreateDirectory(GeneratedMaterialFolder);
        Directory.CreateDirectory(GeneratedControllerFolder);
        ForceReimportLilToonShaders();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        Scene scene = EditorSceneManager.OpenScene(UrpHostScenePath, OpenSceneMode.Single);
        QualitySettings.antiAliasing = 8;

        GameObject existingOrigin = GameObject.Find(PlacementOriginName);
        GameObject existingRoot = GameObject.Find(IntegrationRootName);
        SavedPlacement savedPlacement = CaptureSavedPlacement(existingOrigin, existingRoot);

        if (existingOrigin != null)
        {
            UnityEngine.Object.DestroyImmediate(existingOrigin);
        }

        existingRoot = GameObject.Find(IntegrationRootName);
        if (existingRoot != null)
        {
            UnityEngine.Object.DestroyImmediate(existingRoot);
        }

        float floorY = EstimateSceneFloorY();
        Vector3 petCenter = savedPlacement.HasOrigin ? savedPlacement.OriginPosition : new Vector3(-1.95f, floorY + 1.05f, 1.8f);
        GameObject origin = new GameObject(PlacementOriginName);
        origin.transform.position = petCenter;
        origin.transform.rotation = savedPlacement.HasOrigin ? savedPlacement.OriginRotation : Quaternion.identity;
        origin.transform.localScale = savedPlacement.HasOrigin ? savedPlacement.OriginScale : Vector3.one;

        GameObject root = new GameObject(IntegrationRootName);
        root.transform.SetParent(origin.transform, false);
        root.transform.localPosition = savedPlacement.HasRoot ? savedPlacement.RootLocalPosition : Vector3.zero;
        root.transform.localRotation = savedPlacement.HasRoot ? savedPlacement.RootLocalRotation : Quaternion.identity;
        root.transform.localScale = savedPlacement.HasRoot ? savedPlacement.RootLocalScale : Vector3.one;

        Camera camera = EnsureUrpHostCamera(petCenter);
        Light petLight = CreateUrpPetLight(root.transform, petCenter);

        GameObject petBody = CreatePetBody();
        Animator petAnimator = ConfigureIdleAnimator(petBody);
        petBody.transform.SetParent(root.transform, false);
        RestoreSavedPetBodyTransform(petBody.transform, savedPlacement);

        TransparentWindowController window = root.AddComponent<TransparentWindowController>();
        window.configureNativeWindow = false;
        window.transparentBackground = false;
        window.route = TransparentPetRoute.SceneHost;
        window.alwaysOnTop = false;
        window.clickThroughOutsideHit = false;
        window.transparentCamera = camera;
        window.hitRoot = petBody.transform;
        window.windowSettingsKey = "ScenePet.Window.v1";
        window.moveToSecondaryMonitorOnStart = true;
        window.preferredMonitorIndex = 0;
        window.resizeToTargetMonitorWorkArea = false;
        window.useFullMonitorBounds = false;
        window.compensateMonitorDpiScale = true;
        window.presentationMode = TransparentWindowController.MonitorPresentationMode.SmallWindow;
        window.orientationMode = TransparentWindowController.MonitorOrientationMode.FollowMonitor;
        window.monitorNormalizedPosition = new Vector2(1f, 0.5f);
        window.monitorPaddingPixels = new Vector2Int(24, 48);
        window.primaryRightWindowSizePixels = new Vector2Int(720, 960);
        window.primaryRightWindowPaddingPixels = new Vector2Int(24, 48);

        TransparentPetPerformanceController performanceController = root.AddComponent<TransparentPetPerformanceController>();
        performanceController.settingsKey = "ScenePet.Performance.v1";
        performanceController.limitFrameRate = true;
        performanceController.targetFrameRate = 60;
        performanceController.verticalSync = false;
        performanceController.msaaSamples = 4;
        performanceController.overrideRenderScale = false;

        TransparentPetKawaiiActionController actionController = petBody.AddComponent<TransparentPetKawaiiActionController>();
        actionController.modelRoot = petBody.transform;
        actionController.actionManifestFile = "KawaiiUnity/official_actions.txt";
        actionController.actionBundleDirectory = "GodotFinal/assets/action_import/kawaii100";
        actionController.defaultActionName = "KA_Idle01_breathing";
        actionController.idleActionName = "KA_Idle01_breathing";
        actionController.onlyIdleActions = true;
        actionController.autoPlay = true;
        actionController.randomAutoSwitch = true;
        actionController.useProductRandomActionWhitelist = true;
        actionController.randomActionIntervalSeconds = 8f;
        actionController.useAnimatorController = true;
        actionController.menuOnly = false;
        actionController.applyBundleBoneRotations = false;
        actionController.applyHandFingerRotations = false;
        actionController.transitionSeconds = 0.62f;

        PetExpressionController expressionController = petBody.AddComponent<PetExpressionController>();
        expressionController.scanRoot = petBody.transform;
        expressionController.expressionMapPath = "GodotFinal/config/expression_map.json";
        expressionController.logBlendShapeReport = true;

        PetBlinkController blinkController = petBody.AddComponent<PetBlinkController>();
        blinkController.scanRoot = petBody.transform;
        blinkController.expressionController = expressionController;

        PetMouthController mouthController = petBody.AddComponent<PetMouthController>();
        mouthController.expressionController = expressionController;
        mouthController.driveFromAudio = false;
        mouthController.smoothSpeed = 22f;
        mouthController.externalHoldSeconds = 0.38f;
        mouthController.mouthFlapOpenWeight = 0.72f;
        mouthController.audioMouthTimeoutSeconds = 0.75f;
        mouthController.minAudioMouthOpen = 0.08f;
        mouthController.audioMouthUseVolumeVisemes = true;
        mouthController.audioMouthClosedThreshold = 0.035f;
        mouthController.audioMouthPeakThreshold = 0.82f;
        mouthController.audioMouthHoldMinSeconds = 0.1f;
        mouthController.audioMouthHoldMaxSeconds = 0.2f;

        PetBubbleController bubbleController = petBody.AddComponent<PetBubbleController>();
        bubbleController.searchRoot = petBody.transform;
        bubbleController.characterAnimator = petAnimator;
        bubbleController.worldCamera = camera;
        bubbleController.worldOffset = new Vector3(0f, 0.5f, 0f);
        bubbleController.defaultVisibleSeconds = 4.2f;
        bubbleController.maxVisibleMessages = 3;
        bubbleController.bubbleSize = new Vector2(210f, 48f);
        bubbleController.minBubbleWidth = 54f;
        bubbleController.minBubbleHeight = 26f;
        bubbleController.horizontalPadding = 10f;
        bubbleController.verticalPadding = 5f;
        bubbleController.bubbleSpacing = 5f;
        bubbleController.fontSize = 15;
        bubbleController.maxMessageCharacters = 96;
        bubbleController.mergeWindowSeconds = 1.4f;
        bubbleController.afterSpeechVisibleSeconds = 1.0f;
        bubbleController.canvasScale = 0.0039f;

        SceneSubtitleController subtitleController = petBody.AddComponent<SceneSubtitleController>();
        subtitleController.fontSize = 34;
        subtitleController.maxCharacters = 108;
        subtitleController.maxWidthFraction = 0.78f;
        subtitleController.minWidth = 420f;
        subtitleController.maxWidth = 1180f;
        subtitleController.subtitleHeight = 96f;
        subtitleController.bottomOffset = 58f;
        subtitleController.defaultVisibleSeconds = 4.5f;
        subtitleController.afterSpeechVisibleSeconds = 1.15f;
        subtitleController.mergeWindowSeconds = 0.9f;
        subtitleController.playSegmentsSequentially = true;
        subtitleController.splitLongSubtitleIntoSegments = true;
        subtitleController.segmentMaxCharacters = 42;
        subtitleController.maxQueuedSegments = 8;
        subtitleController.minSegmentVisibleSeconds = 1.25f;
        subtitleController.maxSegmentVisibleSeconds = 3.6f;
        subtitleController.charactersPerSecond = 13f;

        PetStateController stateController = petBody.AddComponent<PetStateController>();
        stateController.animator = petAnimator;
        stateController.actionController = actionController;
        stateController.expressionController = expressionController;
        stateController.mouthController = mouthController;
        stateController.bubbleController = bubbleController;
        stateController.screenSubtitleController = subtitleController;
        stateController.enableSceneScreenSubtitles = true;
        stateController.screenSubtitlesOnlyInSceneHost = true;
        stateController.initialState = "idle";
        stateController.idleActionName = "KA_Idle01_breathing";
        stateController.speakingActionName = "KA_Idle50_StandingTalk1_1";
        stateController.speakingActionPool = CreateSpeakingActionPool();
        stateController.thinkingActionName = "KA_Idle08_ComeUpWithAnIdea";
        stateController.listeningActionName = "KA_Idle02_LookLeftAndRight";
        stateController.happyActionName = "KA_Idle28_Laugh";
        stateController.angryActionName = "KA_Idle27_Angry";
        stateController.surprisedActionName = "KA_Idle29_Surprised";
        stateController.sleepyActionName = "KA_Idle09_Waiting";
        stateController.queueBubbleTextForMouth = true;
        stateController.pauseRandomActionsDuringVoice = true;

        TransparentPetSkeletonHitMask skeletonHitMask = petBody.AddComponent<TransparentPetSkeletonHitMask>();
        skeletonHitMask.animator = petAnimator != null ? petAnimator : petBody.GetComponentInChildren<Animator>();
        skeletonHitMask.targetCamera = camera;
        skeletonHitMask.bodyRadiusPixels = 18f;
        skeletonHitMask.headRadiusPixels = 22f;
        skeletonHitMask.limbRadiusPixels = 8f;
        skeletonHitMask.handFootRadiusPixels = 7f;
        window.skeletonHitMask = skeletonHitMask;

        TransparentPetRuntimeControls runtimeControls = petBody.AddComponent<TransparentPetRuntimeControls>();
        runtimeControls.modelRoot = petBody.transform;
        runtimeControls.skeletonHitMask = skeletonHitMask;
        runtimeControls.keyLight = petLight;
        runtimeControls.currentRenderPreset = "DesktopPetSoft";
        runtimeControls.lightMode = 0;
        runtimeControls.defaultForm = TransparentPetRuntimeControls.PetForm.Base;
        runtimeControls.defaultGlassesVisible = true;
        runtimeControls.defaultWingsVisible = false;
        runtimeControls.ApplyConfiguredDefaults();
        EditorUtility.SetDirty(runtimeControls);

        TransparentPetHeadLookAt headLookAt = ConfigureHeadLookAt(petAnimator, petBody, camera);
        headLookAt.settingsKey = "ScenePet.HeadLookAt.v1";

        TransparentPetFreeCamera freeCamera = camera.GetComponent<TransparentPetFreeCamera>();
        if (freeCamera == null)
        {
            freeCamera = camera.gameObject.AddComponent<TransparentPetFreeCamera>();
        }
        freeCamera.windowController = window;
        freeCamera.targetCamera = camera;
        freeCamera.enabledInput = true;
        freeCamera.target = root.transform.TransformPoint(new Vector3(0f, 0.75f, 0f));
        freeCamera.distance = Mathf.Clamp(Vector3.Distance(camera.transform.position, freeCamera.target), 0.25f, 0.41f);
        freeCamera.requirePetHitForInput = true;
        freeCamera.freeSceneInput = false;
        freeCamera.depthOfFieldEnabled = true;
        freeCamera.lockDepthOfFieldToPet = true;
        freeCamera.cameraSaveKey = "ScenePet.FreeCamera.v1";

        TransparentPetSceneFaceTracker sceneFaceTracker = root.AddComponent<TransparentPetSceneFaceTracker>();
        sceneFaceTracker.windowController = window;
        sceneFaceTracker.freeCamera = freeCamera;
        sceneFaceTracker.headLookAt = headLookAt;
        sceneFaceTracker.targetCamera = camera;
        sceneFaceTracker.settingsKey = "ScenePet.FaceTracking.v3";
        sceneFaceTracker.trackingBackend = TransparentPetFaceTrackingBackend.ExternalMediaPipe;
        sceneFaceTracker.trackingEnabled = true;
        sceneFaceTracker.headFollowEnabled = true;
        sceneFaceTracker.cameraParallaxEnabled = true;
        sceneFaceTracker.cameraOrbitEnabled = true;
        sceneFaceTracker.globalTrackingEnabled = false;
        sceneFaceTracker.mirrorHorizontal = true;
        sceneFaceTracker.mirrorVertical = true;
        sceneFaceTracker.launchExternalProcess = true;
        sceneFaceTracker.startCameraOnEnable = true;
        sceneFaceTracker.trackingAnchor = TransparentPetFaceTrackingAnchor.Head;
        sceneFaceTracker.cameraSightMode = TransparentPetCameraSightMode.ModelAxis;
        sceneFaceTracker.normalizedDeadZone = 0.07f;
        sceneFaceTracker.normalizedDepthDeadZone = 0.05f;
        sceneFaceTracker.offsetSmoothTime = 0.3f;
        sceneFaceTracker.depthSmoothTime = 0.32f;
        sceneFaceTracker.cameraTargetShiftMeters = 0.08f;
        sceneFaceTracker.cameraDepthShiftMeters = 0.06f;
        sceneFaceTracker.cameraHeightFollowMeters = 0.55f;
        sceneFaceTracker.globalTrackingLateralMeters = 1.35f;
        sceneFaceTracker.globalTrackingHeightMeters = 1.8f;
        sceneFaceTracker.globalTrackingDepthMeters = 1.1f;
        sceneFaceTracker.cameraOrbitDeadZoneDegrees = 5f;
        sceneFaceTracker.cameraOrbitSmoothTime = 0.32f;
        sceneFaceTracker.cameraYawOrbitStrength = 1f;
        sceneFaceTracker.cameraPitchOrbitStrength = 0.35f;

        TransparentPetPlacementController placementController = root.AddComponent<TransparentPetPlacementController>();
        placementController.tunedOrigin = origin.transform;
        placementController.offsetFromOrigin = root.transform.localPosition;
        placementController.uniformScale = Mathf.Max(0.01f, root.transform.localScale.x);
        placementController.eulerDegrees = root.transform.localEulerAngles;
        placementController.applyInspectorValues = false;
        placementController.freeCamera = freeCamera;
        placementController.cameraTargetLocalOffset = new Vector3(0f, 0.75f, 0f);
        placementController.lockCameraTargetToPet = true;
        placementController.placementSaveKey = "ScenePet.Placement.v1";
        placementController.CaptureCurrentTransform();

        TransparentPetWorkshopManager workshopManager = root.AddComponent<TransparentPetWorkshopManager>();
        workshopManager.modelRoot = petBody.transform;
        workshopManager.runtimeControls = runtimeControls;
        workshopManager.actionController = actionController;
        workshopManager.headLookAt = headLookAt;
        workshopManager.skeletonHitMask = skeletonHitMask;
        workshopManager.expressionController = expressionController;
        workshopManager.blinkController = blinkController;
        workshopManager.windowController = window;
        workshopManager.targetCamera = camera;

        TransparentPetContextMenu contextMenu = root.AddComponent<TransparentPetContextMenu>();
        contextMenu.windowController = window;
        contextMenu.route = TransparentPetRoute.SceneHost;
        contextMenu.freeCamera = freeCamera;
        contextMenu.runtimeControls = runtimeControls;
        contextMenu.actionController = actionController;
        contextMenu.placementController = placementController;
        contextMenu.headLookAt = headLookAt;
        contextMenu.sceneFaceTracker = sceneFaceTracker;
        contextMenu.performanceController = performanceController;
        contextMenu.workshopManager = workshopManager;
        contextMenu.panelSize = new Vector2(520f, 560f);
        freeCamera.contextMenu = contextMenu;
        performanceController.targetCameras = new[] { camera };

        PetControlServer petControlServer = root.AddComponent<PetControlServer>();
        petControlServer.stateController = stateController;
        petControlServer.host = "127.0.0.1";
        petControlServer.port = 17861;
        petControlServer.startOnPlay = true;
        petControlServer.logCommands = false;

        TransparentPetVoiceRuntimeLauncher voiceLauncher = root.AddComponent<TransparentPetVoiceRuntimeLauncher>();
        voiceLauncher.streamingRootRelativePath = "GodotFinal";
        voiceLauncher.presentationRoute = "unity";
        voiceLauncher.presentationHost = "127.0.0.1";
        voiceLauncher.presentationPort = petControlServer.port;
        voiceLauncher.selectedRouteId = "traditional_vision";
        voiceLauncher.companionPollingIntervalSec = 8;
        voiceLauncher.realtimeMonitoringIntervalSec = 8;
        voiceLauncher.sceneFaceTracker = sceneFaceTracker;
        voiceLauncher.startOnPlay = false;
        voiceLauncher.allowStartOnPlayInProduct = false;
        contextMenu.voiceLauncher = voiceLauncher;

        ConfigureSceneHostPlayerSettings();

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeGameObject = root;
        Debug.Log("Transparent pet integrated into URP host scene: " + UrpHostScenePath);
    }

    private static void ConfigureSceneHostPlayerSettings()
    {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(UrpHostScenePath, true) };
        PlayerSettings.companyName = "LocalDesktopPet";
        PlayerSettings.productName = "ScenePet";
        PlayerSettings.runInBackground = true;
        PlayerSettings.defaultScreenWidth = 1920;
        PlayerSettings.defaultScreenHeight = 1080;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.forceSingleInstance = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        PlayerSettings.useFlipModelSwapchain = false;
    }

    public static void LogLilToonShaderSupport()
    {
        ForceReimportLilToonShaders();
        LogShaderSupport("lilToon");
        LogShaderSupport("Hidden/ltspass_opaque");
        LogShaderSupport("Hidden/lilToonOutline");
    }

    private static SavedPlacement CaptureSavedPlacement(GameObject existingOrigin, GameObject existingRoot)
    {
        SavedPlacement saved = default;

        if (existingRoot == null && existingOrigin != null)
        {
            Transform rootChild = existingOrigin.transform.Find(IntegrationRootName);
            if (rootChild != null)
            {
                existingRoot = rootChild.gameObject;
            }
        }

        if (existingOrigin != null)
        {
            saved.HasOrigin = true;
            saved.OriginPosition = existingOrigin.transform.position;
            saved.OriginRotation = existingOrigin.transform.rotation;
            saved.OriginScale = existingOrigin.transform.localScale;
        }
        else if (existingRoot != null)
        {
            saved.HasOrigin = true;
            saved.OriginPosition = existingRoot.transform.position;
            saved.OriginRotation = Quaternion.identity;
            saved.OriginScale = Vector3.one;
        }

        if (existingRoot != null)
        {
            saved.HasRoot = true;
            saved.RootLocalPosition = existingOrigin != null ? existingRoot.transform.localPosition : Vector3.zero;
            saved.RootLocalRotation = existingOrigin != null ? existingRoot.transform.localRotation : existingRoot.transform.rotation;
            saved.RootLocalScale = existingRoot.transform.localScale;
        }

        Transform existingModel = FindSavedModel(existingRoot, existingOrigin);
        if (existingModel != null)
        {
            saved.HasModelLocal = true;
            saved.ModelLocalPosition = existingModel.localPosition;
            saved.ModelLocalRotation = existingModel.localRotation;
            saved.ModelLocalScale = existingModel.localScale;
        }

        return saved;
    }

    private static void RestoreSavedPetBodyTransform(Transform petBody, SavedPlacement saved)
    {
        if (!saved.HasModelLocal || petBody == null)
        {
            return;
        }

        petBody.localPosition = saved.ModelLocalPosition;
        petBody.localRotation = saved.ModelLocalRotation;
        petBody.localScale = saved.ModelLocalScale;
    }

    private static Transform FindSavedModel(GameObject existingRoot, GameObject existingOrigin)
    {
        Transform model = FindChildByName(existingRoot != null ? existingRoot.transform : null, ModelInstanceName);
        if (model != null)
        {
            return model;
        }

        model = FindChildByName(existingOrigin != null ? existingOrigin.transform : null, ModelInstanceName);
        if (model != null)
        {
            return model;
        }

        GameObject direct = GameObject.Find(ModelInstanceName);
        return direct != null ? direct.transform : null;
    }

    private static Transform FindChildByName(Transform root, string childName)
    {
        if (root == null)
        {
            return null;
        }

        if (root.name == childName)
        {
            return root;
        }

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindChildByName(root.GetChild(i), childName);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static float EstimateSceneFloorY()
    {
        Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude);
        bool hasBounds = false;
        Bounds bounds = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i].GetComponentInParent<Canvas>() != null)
            {
                continue;
            }

            if (!hasBounds)
            {
                bounds = renderers[i].bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
        }

        return hasBounds ? bounds.min.y : 0f;
    }

    private static Camera EnsureUrpHostCamera(Vector3 petCenter)
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            camera = UnityEngine.Object.FindAnyObjectByType<Camera>();
        }

        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            camera = cameraObject.AddComponent<Camera>();
        }

        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 38f;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 200f;
        camera.allowMSAA = true;

        if (camera.transform.position == Vector3.zero)
        {
            camera.transform.position = petCenter + new Vector3(0.2f, 1.25f, -4.2f);
            LookAt(camera.transform, petCenter + new Vector3(0f, 0.72f, 0f));
        }

        return camera;
    }

    private static Light CreateUrpPetLight(Transform parent, Vector3 petCenter)
    {
        GameObject lightObject = new GameObject("TransparentPet Key Light");
        lightObject.transform.SetParent(parent, false);
        lightObject.transform.position = petCenter + new Vector3(0.6f, 1.8f, -1.0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 4.2f;
        light.intensity = 0.22f;
        light.color = new Color(0.9f, 0.92f, 0.98f, 1f);
        return light;
    }

    private static void LookAt(Transform transform, Vector3 target)
    {
        Vector3 direction = target - transform.position;
        if (direction.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }
    }

    private static Animator ConfigureIdleAnimator(GameObject petBody)
    {
        if (petBody == null)
        {
            return null;
        }

        Animator animator = petBody.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            return null;
        }

        RuntimeAnimatorController controller = CreateIdleAnimatorController();
        if (controller == null)
        {
            return animator;
        }

        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.updateMode = AnimatorUpdateMode.Normal;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        Debug.Log("Idle animator assigned: " + controller.name);
        return animator;
    }

    private static TransparentPetHeadLookAt ConfigureHeadLookAt(Animator animator, GameObject petBody, Camera camera)
    {
        GameObject host = animator != null ? animator.gameObject : petBody;
        if (host == null)
        {
            return null;
        }

        TransparentPetHeadLookAt headLookAt = host.GetComponent<TransparentPetHeadLookAt>();
        if (headLookAt == null)
        {
            headLookAt = host.AddComponent<TransparentPetHeadLookAt>();
        }

        headLookAt.animator = animator != null ? animator : host.GetComponentInChildren<Animator>();
        headLookAt.targetCamera = camera;
        headLookAt.modelRoot = petBody != null ? petBody.transform : host.transform;
        headLookAt.lookAtEnabled = true;
        headLookAt.deadZoneDegrees = 4f;
        headLookAt.smoothTime = 0.16f;
        headLookAt.maxYawDegrees = 38f;
        headLookAt.maxPitchUpDegrees = 18f;
        headLookAt.maxPitchDownDegrees = 22f;
        headLookAt.ikBodyWeight = 0.02f;
        headLookAt.ikHeadWeight = 0.82f;
        headLookAt.ikClampWeight = 0.72f;
        EditorUtility.SetDirty(headLookAt);
        return headLookAt;
    }

    private static RuntimeAnimatorController CreateIdleAnimatorController()
    {
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(KawaiiAnimatorControllerPath);
        if (controller == null)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(KawaiiAnimatorControllerPath);
        }

        AnimatorControllerLayer layer = controller.layers[0];
        AnimatorStateMachine stateMachine = layer.stateMachine;
        EnableAnimatorIkPass(controller);
        ChildAnimatorState[] existingStates = stateMachine.states;
        for (int i = 0; i < existingStates.Length; i++)
        {
            stateMachine.RemoveState(existingStates[i].state);
        }

        List<string> actionNames = LoadOfficialIdleActionNames();
        AnimatorState defaultState = null;
        int added = 0;
        for (int i = 0; i < actionNames.Count; i++)
        {
            string actionName = actionNames[i];
            AnimationClip clip = LoadKawaiiAnimationClip(actionName);
            if (clip == null)
            {
                Debug.LogWarning("Kawaii animation clip missing: " + actionName);
                continue;
            }

            AnimatorState state = stateMachine.AddState(actionName, new Vector3(220f * (added % 5), 72f * (added / 5), 0f));
            state.motion = clip;
            state.speed = 1f;
            state.writeDefaultValues = true;
            added++;

            if (defaultState == null || actionName == "KA_Idle01_breathing")
            {
                defaultState = state;
            }
        }

        if (defaultState != null)
        {
            stateMachine.defaultState = defaultState;
        }

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Debug.Log("Kawaii animator controller updated with clips: " + added);
        return controller;
    }

    private static void EnableAnimatorIkPass(AnimatorController controller)
    {
        if (controller == null)
        {
            return;
        }

        AnimatorControllerLayer[] layers = controller.layers;
        for (int i = 0; i < layers.Length; i++)
        {
            layers[i].iKPass = true;
        }

        controller.layers = layers;
    }

    private static AnimationClip LoadKawaiiAnimationClip(string actionName)
    {
        string animationPath = ResolveKawaiiAnimationPath(actionName);
        if (!File.Exists(animationPath))
        {
            return null;
        }

        ConfigureKawaiiAnimationImporter(animationPath);
        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(animationPath);
        for (int i = 0; i < assets.Length; i++)
        {
            if (assets[i] is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
            {
                return clip;
            }
        }

        return null;
    }

    private static string ResolveKawaiiAnimationPath(string actionName)
    {
        string fileName = "/@" + actionName + ".FBX";
        string[] candidates =
        {
            KawaiiAnimationFolder + fileName,
            CopiedKawaiiAnimationFolder + fileName,
            KawaiiGenericAnimationFolder + fileName,
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                return candidates[i];
            }
        }

        return candidates[0];
    }

    private static void ConfigureKawaiiAnimationImporter(string animationPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(animationPath) as ModelImporter;
        if (importer == null)
        {
            AssetDatabase.ImportAsset(animationPath, ImportAssetOptions.ForceSynchronousImport);
            return;
        }

        bool changed = false;
        if (!importer.importAnimation)
        {
            importer.importAnimation = true;
            changed = true;
        }

        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            changed = true;
        }

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
        {
            clips = importer.defaultClipAnimations;
        }

        for (int i = 0; i < clips.Length; i++)
        {
            if (!clips[i].loopTime || !clips[i].loopPose || clips[i].wrapMode != WrapMode.Loop)
            {
                clips[i].loopTime = true;
                clips[i].loopPose = true;
                clips[i].wrapMode = WrapMode.Loop;
                clips[i].lockRootRotation = true;
                clips[i].lockRootHeightY = true;
                clips[i].lockRootPositionXZ = true;
                changed = true;
            }
        }

        if (clips != null && clips.Length > 0)
        {
            importer.clipAnimations = clips;
        }

        if (changed)
        {
            importer.SaveAndReimport();
        }
        else
        {
            AssetDatabase.ImportAsset(animationPath, ImportAssetOptions.ForceSynchronousImport);
        }
    }

    private static List<string> LoadOfficialIdleActionNames()
    {
        List<string> actionNames = new List<string>();
        if (!File.Exists(OfficialActionsManifestPath))
        {
            actionNames.Add("KA_Idle01_breathing");
            return actionNames;
        }

        string[] lines = File.ReadAllLines(OfficialActionsManifestPath);
        for (int i = 0; i < lines.Length; i++)
        {
            string assetPath = lines[i].Trim().TrimStart('\uFEFF');
            if (assetPath.Length == 0 || assetPath.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string actionName = ActionNameFromAssetPath(assetPath);
            if (IsUnityIdleActionAllowed(actionName) && !actionNames.Contains(actionName))
            {
                actionNames.Add(actionName);
            }
        }

        return actionNames;
    }

    private static string ActionNameFromAssetPath(string assetPath)
    {
        string normalized = (assetPath ?? string.Empty).Replace('\\', '/');
        int slashIndex = normalized.LastIndexOf('/');
        string fileName = slashIndex >= 0 ? normalized.Substring(slashIndex + 1) : normalized;
        int extensionIndex = fileName.LastIndexOf('.');
        if (extensionIndex > 0)
        {
            fileName = fileName.Substring(0, extensionIndex);
        }

        return fileName.StartsWith("@", StringComparison.Ordinal) ? fileName.Substring(1) : fileName;
    }

    private static bool IsUnityIdleActionAllowed(string actionName)
    {
        int number = IdleActionNumber(actionName);
        if (number < 0)
        {
            return false;
        }

        int[] excluded = { 7, 13, 30, 33, 34, 57, 58 };
        for (int i = 0; i < excluded.Length; i++)
        {
            if (excluded[i] == number)
            {
                return false;
            }
        }

        return true;
    }

    private static int IdleActionNumber(string actionName)
    {
        if (string.IsNullOrEmpty(actionName) || !actionName.StartsWith("KA_Idle", StringComparison.Ordinal))
        {
            return -1;
        }

        int start = "KA_Idle".Length;
        int end = start;
        while (end < actionName.Length && char.IsDigit(actionName[end]))
        {
            end++;
        }

        return end > start && int.TryParse(actionName.Substring(start, end - start), out int value) ? value : -1;
    }

    private static Camera CreateCamera()
    {
        GameObject cameraObject = new GameObject("TransparentCamera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.orthographic = true;
        camera.orthographicSize = 1.65f;
        camera.allowMSAA = true;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 100f;
        camera.transform.position = new Vector3(0f, 0.75f, -5f);
        camera.transform.rotation = Quaternion.identity;
        cameraObject.tag = "MainCamera";
        return camera;
    }

    private static void ConfigureRenderSettings()
    {
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.58f, 0.6f, 0.68f, 1f);
        RenderSettings.ambientIntensity = 0.35f;
        RenderSettings.reflectionIntensity = 0f;
    }

    private static Light CreateLight()
    {
        GameObject lightObject = new GameObject("KeyLight");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.white;
        light.intensity = 0.58f;
        light.transform.rotation = Quaternion.Euler(32f, -12f, 0f);
        return light;
    }

    private static GameObject CreatePetBody()
    {
        GameObject model = CreatePublicPlaceholderModel();
        model.name = ModelInstanceName;
        model.transform.position = Vector3.zero;
        model.transform.rotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;
        FitModelToWindow(model);
        EnsureHitCollider(model);
        return model;
    }

    private static GameObject CreatePublicPlaceholderModel()
    {
        Material bodyMaterial = CreatePublicPlaceholderMaterial(
            PublicPlaceholderMaterialPath,
            "PublicPlaceholder_Desktop",
            new Color(0.55f, 0.68f, 0.92f, 1f));
        Material darkMaterial = CreatePublicPlaceholderMaterial(
            PublicPlaceholderDarkMaterialPath,
            "PublicPlaceholderDark_Desktop",
            new Color(0.08f, 0.1f, 0.16f, 1f));

        GameObject root = new GameObject(ModelInstanceName);
        root.AddComponent<Animator>();

        AddPlaceholderPrimitive(root.transform, PrimitiveType.Capsule, "Body", new Vector3(0f, 0.55f, 0f), Quaternion.identity, new Vector3(0.58f, 0.78f, 0.42f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Sphere, "Head", new Vector3(0f, 1.45f, 0f), Quaternion.identity, new Vector3(0.54f, 0.5f, 0.48f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Capsule, "LeftArm", new Vector3(-0.48f, 0.78f, 0f), Quaternion.Euler(0f, 0f, 18f), new Vector3(0.18f, 0.46f, 0.18f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Capsule, "RightArm", new Vector3(0.48f, 0.78f, 0f), Quaternion.Euler(0f, 0f, -18f), new Vector3(0.18f, 0.46f, 0.18f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Capsule, "LeftLeg", new Vector3(-0.2f, -0.24f, 0f), Quaternion.identity, new Vector3(0.2f, 0.44f, 0.2f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Capsule, "RightLeg", new Vector3(0.2f, -0.24f, 0f), Quaternion.identity, new Vector3(0.2f, 0.44f, 0.2f), bodyMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Sphere, "LeftEye", new Vector3(-0.12f, 1.52f, -0.24f), Quaternion.identity, new Vector3(0.06f, 0.06f, 0.025f), darkMaterial);
        AddPlaceholderPrimitive(root.transform, PrimitiveType.Sphere, "RightEye", new Vector3(0.12f, 1.52f, -0.24f), Quaternion.identity, new Vector3(0.06f, 0.06f, 0.025f), darkMaterial);

        GameObject focus = new GameObject("HeadFocus");
        focus.transform.SetParent(root.transform, false);
        focus.transform.localPosition = new Vector3(0f, 1.45f, -0.22f);
        return root;
    }

    private static void AddPlaceholderPrimitive(
        Transform parent,
        PrimitiveType type,
        string name,
        Vector3 localPosition,
        Quaternion localRotation,
        Vector3 localScale,
        Material material)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = name;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localRotation = localRotation;
        part.transform.localScale = localScale;

        Collider collider = part.GetComponent<Collider>();
        if (collider != null)
        {
            UnityEngine.Object.DestroyImmediate(collider);
        }

        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    private static Material CreatePublicPlaceholderMaterial(string path, string name, Color color)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("lilToon");
            if (shader == null)
            {
                shader = Shader.Find("DesktopPet/SilverWolfSimpleToon");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            material = new Material(shader)
            {
                name = name
            };
            AssetDatabase.CreateAsset(material, path);
        }

        material.name = name;
        SetColorIfExists(material, "_Color", color);
        SetColorIfExists(material, "_BaseColor", color);
        SetColorIfExists(material, "_MainColor", color);
        SetColorIfExists(material, "_ShadowColor", MultiplyRgb(color, 0.65f));
        SetColorIfExists(material, "_Shadow2ndColor", MultiplyRgb(color, 0.48f));
        SetFloatIfExists(material, "_UseOutline", 1f);
        SetFloatIfExists(material, "_OutlineWidth", 0.007f);
        SetFloatIfExists(material, "_AsUnlit", 0.18f);
        SetFloatIfExists(material, "_Cutoff", 0.03f);
        material.renderQueue = (int)RenderQueue.AlphaTest;
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void FitModelToWindow(GameObject model)
    {
        Bounds bounds = CalculateRendererBounds(model);
        if (bounds.size.y <= 0.001f)
        {
            model.transform.position = new Vector3(0f, 0f, 0f);
            model.transform.localScale = Vector3.one * 0.65f;
            return;
        }

        const float targetHeight = 2.85f;
        const float desiredBottom = -1.05f;
        float scale = targetHeight / bounds.size.y;
        model.transform.localScale *= scale;

        bounds = CalculateRendererBounds(model);
        Vector3 offset = new Vector3(-bounds.center.x, desiredBottom - bounds.min.y, -bounds.center.z);
        model.transform.position += offset;
    }

    private static void EnsureHitCollider(GameObject model)
    {
        Bounds bounds = CalculateRendererBounds(model);
        BoxCollider collider = model.GetComponent<BoxCollider>();
        if (collider == null)
        {
            collider = model.AddComponent<BoxCollider>();
        }

        float uniformScale = Mathf.Max(0.0001f, model.transform.lossyScale.x);
        collider.center = model.transform.InverseTransformPoint(bounds.center);
        collider.size = new Vector3(bounds.size.x, bounds.size.y, bounds.size.z) / uniformScale;
        collider.size = new Vector3(
            Mathf.Max(collider.size.x, 0.6f),
            Mathf.Max(collider.size.y, 1.6f),
            Mathf.Max(collider.size.z, 0.35f));
    }

    private static Bounds CalculateRendererBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(root.transform.position, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private static void ApplyGeneratedMaterials(GameObject root)
    {
        Dictionary<string, Material> materialCache = new Dictionary<string, Material>();
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            Material[] sourceMaterials = renderer.sharedMaterials;
            Material[] replacementMaterials = new Material[sourceMaterials.Length];
            bool useLookDevSlotOrder = sourceMaterials.Length == LookDevSlotCount;

            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                Material sourceMaterial = useLookDevSlotOrder ? LoadLookDevMaterial(i) : sourceMaterials[i];
                if (sourceMaterial == null)
                {
                    sourceMaterial = sourceMaterials[i];
                }

                string sourcePath = sourceMaterial != null ? AssetDatabase.GetAssetPath(sourceMaterial) : string.Empty;
                string sourceName = !string.IsNullOrEmpty(sourcePath)
                    ? Path.GetFileNameWithoutExtension(sourcePath)
                    : sourceMaterial != null ? sourceMaterial.name : renderer.name;
                string cacheKey = !string.IsNullOrEmpty(sourcePath) ? sourcePath : sourceName;

                if (!materialCache.TryGetValue(cacheKey, out Material replacement))
                {
                    replacement = CreateGeneratedMaterial(sourceMaterial, sourceName);
                    materialCache[cacheKey] = replacement;
                }

                replacementMaterials[i] = replacement;
            }

            renderer.sharedMaterials = replacementMaterials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        AssetDatabase.SaveAssets();
    }

    private static Material LoadLookDevMaterial(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= LookDevMaterialGuids.Length)
        {
            return null;
        }

        string path = AssetDatabase.GUIDToAssetPath(LookDevMaterialGuids[slotIndex]);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogWarning("LookDev material missing for slot " + slotIndex + ": " + LookDevMaterialGuids[slotIndex]);
            return null;
        }

        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    private static Material CreateGeneratedMaterial(Material sourceMaterial, string sourceName)
    {
        string materialPath = GeneratedMaterialFolder + "/" + SanitizeFileName(sourceName) + "_Desktop.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        MaterialSourceData source = ReadMaterialSourceData(sourceMaterial, sourceName);
        ApplyMaterialTextureOverrides(ref source, sourceName);

        if (UseUnityToonShaderTrial)
        {
            Shader toonShader = Shader.Find("Toon");
            if (toonShader != null)
            {
                if (material == null)
                {
                    material = new Material(toonShader)
                    {
                        name = sourceName + "_Desktop"
                    };
                    AssetDatabase.CreateAsset(material, materialPath);
                }

                material.shader = toonShader;
                material.name = sourceName + "_Desktop";
                ConfigureUnityToonMaterial(material, source, sourceName);
                EditorUtility.SetDirty(material);
                return material;
            }
        }

        Shader lilToonShader = Shader.Find("lilToon");
        if (lilToonShader != null)
        {
            if (material == null)
            {
                material = new Material(lilToonShader)
                {
                    name = sourceName + "_Desktop"
                };
                AssetDatabase.CreateAsset(material, materialPath);
            }

            material.shader = lilToonShader;
            material.name = sourceName + "_Desktop";
            ConfigureLilToonMaterial(material, source, sourceName);
            EditorUtility.SetDirty(material);
            return material;
        }

        if (sourceMaterial != null && sourceMaterial.shader != null)
        {
            if (material == null)
            {
                material = new Material(sourceMaterial)
                {
                    name = sourceName + "_Desktop"
                };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = sourceMaterial.shader;
                material.CopyPropertiesFromMaterial(sourceMaterial);
                material.renderQueue = sourceMaterial.renderQueue;
                material.enableInstancing = sourceMaterial.enableInstancing;
                material.doubleSidedGI = sourceMaterial.doubleSidedGI;
                material.name = sourceName + "_Desktop";
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        if (material == null)
        {
            Shader shader = Shader.Find("lilToon");
            if (shader == null)
            {
                shader = Shader.Find("DesktopPet/SilverWolfSimpleToon");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            material = new Material(shader)
            {
                name = sourceName + "_Desktop"
            };
            AssetDatabase.CreateAsset(material, materialPath);
        }

        if (source.MainTexture != null)
        {
            material.SetTexture("_MainTex", source.MainTexture);
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", source.MainTexture);
            }
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", source.Tint);
        }

        if (material.HasProperty("_ShadowColor"))
        {
            material.SetColor("_ShadowColor", source.ShadowColor);
        }

        if (material.HasProperty("_Alpha"))
        {
            material.SetFloat("_Alpha", source.Alpha);
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    private static void ApplyMaterialTextureOverrides(ref MaterialSourceData source, string sourceName)
    {
        string normalized = NormalizeName(sourceName);
        if (normalized.Contains("\u982d\u98fe", StringComparison.Ordinal) ||
            normalized.Contains("\u93e1\u6846", StringComparison.Ordinal) ||
            normalized.Contains("\u93e1\u7247", StringComparison.Ordinal))
        {
            Texture clothingTexture = AssetDatabase.LoadAssetAtPath<Texture>(TextureFolder + "/\u8863.png");
            if (clothingTexture != null)
            {
                source.MainTexture = clothingTexture;
            }

            return;
        }

        if (normalized.Contains("\u7279\u6548\u533a\u8863\u6446", StringComparison.Ordinal))
        {
            Texture azTexture = AssetDatabase.LoadAssetAtPath<Texture>(TextureFolder + "/a4.bmp");
            if (azTexture != null)
            {
                source.MainTexture = azTexture;
            }
        }
    }

    private static void ConfigureUnityToonMaterial(Material material, MaterialSourceData source, string sourceName)
    {
        SetTextureIfExists(material, "_MainTex", source.MainTexture);
        SetTextureIfExists(material, "_BaseMap", source.MainTexture);
        SetTextureIfExists(material, "_BaseColorMap", source.MainTexture);
        SetTextureIfExists(material, "_1st_ShadeMap", source.MainTexture);
        SetTextureIfExists(material, "_2nd_ShadeMap", source.MainTexture);

        bool isSkin = IsSkinToneMaterial(sourceName);
        bool isHighEnergy = IsHighEnergyMaterial(sourceName);
        float bodyMultiplier = isHighEnergy ? 0.76f : 0.96f;
        Color baseColor = isSkin ? new Color(1.08f, 0.96f, 0.9f, 1f) : MultiplyRgb(source.Tint, bodyMultiplier);
        baseColor.a = Mathf.Clamp01(source.Alpha);
        Color shade1 = isSkin ? new Color(0.98f, 0.82f, 0.76f, baseColor.a) : MultiplyRgb(baseColor, isHighEnergy ? 0.8f : 0.9f);
        Color shade2 = isSkin ? new Color(0.8f, 0.62f, 0.58f, baseColor.a) : MultiplyRgb(baseColor, isHighEnergy ? 0.62f : 0.74f);
        shade1.a = baseColor.a;
        shade2.a = baseColor.a;

        SetColorIfExists(material, "_Color", baseColor);
        SetColorIfExists(material, "_BaseColor", baseColor);
        SetColorIfExists(material, "_1st_ShadeColor", shade1);
        SetColorIfExists(material, "_2nd_ShadeColor", shade2);
        SetColorIfExists(material, "_Outline_Color", new Color(0.88f, 0.62f, 0.55f, 1f));
        SetColorIfExists(material, "_Emissive_Color", Color.black);

        SetFloatIfExists(material, "_isUnityToonshader", 1f);
        SetFloatIfExists(material, "_utsTechnique", 0f);
        SetFloatIfExists(material, "_AutoRenderQueue", 0f);
        SetFloatIfExists(material, "_Use_BaseAs1st", 1f);
        SetFloatIfExists(material, "_Use_1stAs2nd", 1f);
        SetFloatIfExists(material, "_Is_LightColor_Base", 0f);
        SetFloatIfExists(material, "_Is_LightColor_1st_Shade", 0f);
        SetFloatIfExists(material, "_Is_LightColor_2nd_Shade", 0f);
        SetFloatIfExists(material, "_Set_SystemShadowsToBase", 0f);
        SetFloatIfExists(material, "_BaseColor_Step", isSkin ? 0.68f : isHighEnergy ? 0.5f : 0.62f);
        SetFloatIfExists(material, "_BaseShade_Feather", isSkin ? 0.1f : isHighEnergy ? 0.06f : 0.09f);
        SetFloatIfExists(material, "_1st_ShadeColor_Step", isSkin ? 0.28f : isHighEnergy ? 0.32f : 0.24f);
        SetFloatIfExists(material, "_1st_ShadeColor_Feather", isSkin ? 0.12f : isHighEnergy ? 0.06f : 0.1f);
        SetFloatIfExists(material, "_2nd_ShadeColor_Step", isSkin ? 0.04f : isHighEnergy ? 0.06f : 0.03f);
        SetFloatIfExists(material, "_2nd_ShadeColor_Feather", isSkin ? 0.08f : isHighEnergy ? 0.04f : 0.07f);
        SetFloatIfExists(material, "_GI_Intensity", isSkin ? 0.06f : isHighEnergy ? 0f : 0.03f);
        SetFloatIfExists(material, "_Unlit_Intensity", isSkin ? 0.58f : isHighEnergy ? 0.14f : 0.34f);
        SetFloatIfExists(material, "_EMISSIVE", 0f);
        SetFloatIfExists(material, "_Outline_Width", 0.008f);
        SetFloatIfExists(material, "_OUTLINE", 0f);
        SetFloatIfExists(material, "_CullMode", 2f);
        SetFloatIfExists(material, "_ZWriteMode", 1f);
        SetFloatIfExists(material, "_ZWrite", 1f);
        SetFloatIfExists(material, "_TransparentEnabled", 0f);
        SetFloatIfExists(material, "_ClippingMode", 1f);
        SetFloatIfExists(material, "_IsBaseMapAlphaAsClippingMask", 1f);
        SetFloatIfExists(material, "_Clipping_Level", 0.03f);
        SetFloatIfExists(material, "_Tweak_transparency", 0f);

        material.EnableKeyword("_IS_CLIPPING_MODE");
        material.DisableKeyword("_IS_CLIPPING_OFF");
        material.DisableKeyword("_IS_CLIPPING_TRANSMODE");
        material.EnableKeyword("_IS_OUTLINE_CLIPPING_YES");
        material.DisableKeyword("_IS_OUTLINE_CLIPPING_NO");
        material.EnableKeyword("_OUTLINE_NML");
        material.DisableKeyword("_OUTLINE_POS");
        material.DisableKeyword("_DISABLE_OUTLINE");
        material.DisableKeyword("_EMISSIVE_SIMPLE");
        material.DisableKeyword("_EMISSIVE_ANIMATION");
        material.SetShaderPassEnabled("Always", true);
        material.SetOverrideTag("RenderType", "TransparentCutOut");
        material.SetOverrideTag("IgnoreProjection", "True");
        material.renderQueue = (int)RenderQueue.AlphaTest;
    }

    private static void ConfigureLilToonMaterial(Material material, MaterialSourceData source, string sourceName)
    {
        material.shaderKeywords = Array.Empty<string>();
        SetTextureIfExists(material, "_MainTex", source.MainTexture);

        bool isSkin = IsSkinToneMaterial(sourceName);
        bool isHighEnergy = IsHighEnergyMaterial(sourceName);
        float bodyMultiplier = isHighEnergy ? 0.66f : 0.84f;
        Color baseColor = isSkin ? new Color(0.96f, 0.86f, 0.8f, 1f) : MultiplyRgb(source.Tint, bodyMultiplier);
        baseColor.a = Mathf.Clamp01(source.Alpha);
        Color shadowColor = isSkin ? new Color(0.82f, 0.62f, 0.56f, baseColor.a) : MultiplyRgb(baseColor, isHighEnergy ? 0.72f : 0.7f);
        Color shadow2Color = isSkin ? new Color(0.68f, 0.48f, 0.44f, baseColor.a) : MultiplyRgb(baseColor, isHighEnergy ? 0.52f : 0.54f);
        shadowColor.a = baseColor.a;
        shadow2Color.a = baseColor.a;

        SetColorIfExists(material, "_Color", baseColor);
        SetColorIfExists(material, "_ShadowColor", shadowColor);
        SetColorIfExists(material, "_Shadow2ndColor", shadow2Color);
        SetColorIfExists(material, "_Shadow3rdColor", new Color(0f, 0f, 0f, 0f));
        SetColorIfExists(material, "_BackfaceColor", new Color(0f, 0f, 0f, 0f));
        SetColorIfExists(material, "_OutlineColor", new Color(0.88f, 0.62f, 0.55f, 1f));
        SetColorIfExists(material, "_EmissionColor", isHighEnergy ? new Color(0.22f, 0.42f, 0.9f, 1f) : Color.black);
        SetColorIfExists(material, "_Emission2ndColor", Color.black);

        SetFloatIfExists(material, "_AsUnlit", isHighEnergy ? 0.08f : 0.16f);
        SetFloatIfExists(material, "_LightMinLimit", isSkin ? 0.04f : 0.03f);
        SetFloatIfExists(material, "_LightMaxLimit", isHighEnergy ? 0.52f : 0.62f);
        SetFloatIfExists(material, "_BeforeExposureLimit", 0.74f);
        SetFloatIfExists(material, "_lilDirectionalLightStrength", 0.32f);
        SetFloatIfExists(material, "_VertexLightStrength", 0.08f);
        SetFloatIfExists(material, "_MonochromeLighting", 0f);
        SetFloatIfExists(material, "_AAStrength", 0.72f);

        SetFloatIfExists(material, "_UseShadow", 1f);
        SetFloatIfExists(material, "_ShadowStrength", isHighEnergy ? 0.36f : 0.52f);
        SetFloatIfExists(material, "_ShadowBorder", isSkin ? 0.54f : 0.58f);
        SetFloatIfExists(material, "_ShadowBlur", isSkin ? 0.13f : 0.1f);
        SetFloatIfExists(material, "_ShadowReceive", 0f);
        SetFloatIfExists(material, "_Shadow2ndBorder", isSkin ? 0.22f : 0.24f);
        SetFloatIfExists(material, "_Shadow2ndBlur", 0.1f);
        SetFloatIfExists(material, "_Shadow2ndReceive", 0f);
        SetFloatIfExists(material, "_ShadowMainStrength", 0f);
        SetFloatIfExists(material, "_ShadowEnvStrength", 0f);
        SetFloatIfExists(material, "_ShadowPostAO", 0f);

        SetFloatIfExists(material, "_UseEmission", isHighEnergy ? 1f : 0f);
        SetFloatIfExists(material, "_EmissionMainStrength", 0f);
        SetFloatIfExists(material, "_EmissionBlend", isHighEnergy ? 0.22f : 0f);
        SetFloatIfExists(material, "_EmissionFluorescence", 0f);
        SetFloatIfExists(material, "_UseEmission2nd", 0f);

        SetFloatIfExists(material, "_UseOutline", 1f);
        SetFloatIfExists(material, "_OutlineWidth", 0.008f);
        SetFloatIfExists(material, "_OutlineFixWidth", 0.5f);
        SetFloatIfExists(material, "_OutlineEnableLighting", 0.08f);
        SetFloatIfExists(material, "_OutlineLitApplyTex", 0f);
        SetFloatIfExists(material, "_OutlineLitScale", 0f);
        SetFloatIfExists(material, "_OutlineLitOffset", 0f);

        SetFloatIfExists(material, "_TransparentMode", 1f);
        SetFloatIfExists(material, "_Cutoff", 0.03f);
        SetFloatIfExists(material, "_SubpassCutoff", 0.03f);
        SetFloatIfExists(material, "_Cull", 2f);
        SetFloatIfExists(material, "_ZWrite", 1f);
        SetFloatIfExists(material, "_SrcBlend", 1f);
        SetFloatIfExists(material, "_DstBlend", 0f);
        SetFloatIfExists(material, "_AlphaToMask", 0f);

        material.SetOverrideTag("RenderType", "TransparentCutout");
        material.renderQueue = (int)RenderQueue.AlphaTest;
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        TrySetupLilToonMaterial(material);
    }

    private static void TrySetupLilToonMaterial(Material material)
    {
        Type utilityType = Type.GetType("lilToon.lilMaterialUtils, lilToon.Editor");
        MethodInfo setupMethod = utilityType?.GetMethod(
            "SetupMultiMaterial",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Material) },
            null);
        setupMethod?.Invoke(null, new object[] { material });
    }

    private static void ForceReimportLilToonShaders()
    {
        string[] shaderPaths =
        {
            "Packages/jp.lilxyzw.liltoon/Shader/ltspass_opaque.shader",
            "Packages/jp.lilxyzw.liltoon/Shader/lts.shader",
            "Packages/jp.lilxyzw.liltoon/Shader/lts_o.shader"
        };

        foreach (string shaderPath in shaderPaths)
        {
            if (File.Exists(shaderPath))
            {
                AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }
    }

    private static void LogShaderSupport(string shaderName)
    {
        Shader shader = Shader.Find(shaderName);
        Debug.Log($"TransparentPet shader support: {shaderName} found={shader != null} supported={(shader != null && shader.isSupported)}");
    }

    private static bool IsSkinToneMaterial(string sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return false;
        }

        if (sourceName.Contains("\u808c", StringComparison.Ordinal) ||
            sourceName.Contains("\u984f", StringComparison.Ordinal) ||
            sourceName.Contains("\u989c", StringComparison.Ordinal) ||
            sourceName.Contains("\u81c9", StringComparison.Ordinal) ||
            sourceName.Contains("\u8138", StringComparison.Ordinal))
        {
            return true;
        }

        return sourceName.IndexOf("skin", StringComparison.OrdinalIgnoreCase) >= 0 ||
            sourceName.IndexOf("face", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHighEnergyMaterial(string sourceName)
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return false;
        }

        return sourceName.Contains("\u5149", StringComparison.Ordinal) ||
            sourceName.Contains("\u7ffc", StringComparison.Ordinal) ||
            sourceName.Contains("\u7279\u6548", StringComparison.Ordinal) ||
            sourceName.Contains("\u706f", StringComparison.Ordinal) ||
            sourceName.Contains("\u71c8", StringComparison.Ordinal) ||
            sourceName.IndexOf("al", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SetTextureIfExists(Material material, string propertyName, Texture texture)
    {
        if (texture != null && material.HasProperty(propertyName))
        {
            material.SetTexture(propertyName, texture);
        }
    }

    private static void SetColorIfExists(Material material, string propertyName, Color color)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, color);
        }
    }

    private static void SetFloatIfExists(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private static Color MultiplyRgb(Color color, float multiplier)
    {
        return new Color(
            Mathf.Clamp01(color.r * multiplier),
            Mathf.Clamp01(color.g * multiplier),
            Mathf.Clamp01(color.b * multiplier),
            color.a);
    }

    private static MaterialSourceData ReadMaterialSourceData(Material sourceMaterial, string sourceName)
    {
        MaterialSourceData data = new MaterialSourceData
        {
            MainTexture = null,
            Tint = Color.white,
            ShadowColor = new Color(0.62f, 0.58f, 0.78f, 1f),
            Alpha = 1f
        };

        string sourcePath = sourceMaterial != null ? AssetDatabase.GetAssetPath(sourceMaterial) : string.Empty;
        if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
        {
            string yaml = File.ReadAllText(sourcePath);
            data.MainTexture = LoadTextureFromMaterialYaml(yaml);

            if (TryReadColorFromMaterialYaml(yaml, "_Color", out Color color) ||
                TryReadColorFromMaterialYaml(yaml, "_BaseColor", out color) ||
                TryReadColorFromMaterialYaml(yaml, "_MainColor", out color))
            {
                data.Tint = color;
            }

            if (TryReadColorFromMaterialYaml(yaml, "_ShadeColor", out Color shadeColor) ||
                TryReadColorFromMaterialYaml(yaml, "_ShadowColor", out shadeColor))
            {
                data.ShadowColor = shadeColor;
            }

            if (TryReadFloatFromMaterialYaml(yaml, "_Alpha", out float alpha))
            {
                data.Alpha = Mathf.Clamp01(alpha);
            }
        }

        if (data.MainTexture == null)
        {
            data.MainTexture = FindTextureForMaterial(sourceMaterial, sourceName);
        }

        data.Alpha *= data.Tint.a;
        data.Tint.a = 1f;
        return data;
    }

    private static Texture LoadTextureFromMaterialYaml(string yaml)
    {
        string[] textureProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };
        foreach (string property in textureProperties)
        {
            string guid = FindTextureGuidInMaterialYaml(yaml, property);
            if (string.IsNullOrEmpty(guid))
            {
                continue;
            }

            string texturePath = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(texturePath))
            {
                Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (texture != null)
                {
                    return texture;
                }
            }
        }

        return null;
    }

    private static string FindTextureGuidInMaterialYaml(string yaml, string propertyName)
    {
        using (StringReader reader = new StringReader(yaml))
        {
            bool inProperty = false;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("- " + propertyName + ":", StringComparison.Ordinal))
                {
                    inProperty = true;
                    continue;
                }

                if (inProperty && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    return null;
                }

                if (inProperty)
                {
                    string guid = ExtractGuid(line);
                    if (!string.IsNullOrEmpty(guid))
                    {
                        return guid;
                    }
                }
            }
        }

        return null;
    }

    private static string ExtractGuid(string line)
    {
        const string marker = "guid:";
        int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        int start = markerIndex + marker.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        int end = start;
        while (end < line.Length && Uri.IsHexDigit(line[end]))
        {
            end++;
        }

        return end > start ? line.Substring(start, end - start) : null;
    }

    private static bool TryReadColorFromMaterialYaml(string yaml, string propertyName, out Color color)
    {
        using (StringReader reader = new StringReader(yaml))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("- " + propertyName + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryReadFloatComponent(line, "r:", out float r) &&
                    TryReadFloatComponent(line, "g:", out float g) &&
                    TryReadFloatComponent(line, "b:", out float b) &&
                    TryReadFloatComponent(line, "a:", out float a))
                {
                    color = new Color(r, g, b, a);
                    return true;
                }
            }
        }

        color = Color.white;
        return false;
    }

    private static bool TryReadFloatFromMaterialYaml(string yaml, string propertyName, out float value)
    {
        using (StringReader reader = new StringReader(yaml))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("- " + propertyName + ":", StringComparison.Ordinal))
                {
                    continue;
                }

                int colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0 &&
                    float.TryParse(trimmed.Substring(colonIndex + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }
        }

        value = 0f;
        return false;
    }

    private static bool TryReadFloatComponent(string line, string marker, out float value)
    {
        int searchStart = line.IndexOf('{');
        if (searchStart < 0)
        {
            searchStart = 0;
        }

        int start = line.IndexOf(marker, searchStart, StringComparison.Ordinal);
        if (start < 0)
        {
            value = 0f;
            return false;
        }

        start += marker.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        int end = start;
        while (end < line.Length && "-+.0123456789eE".IndexOf(line[end]) >= 0)
        {
            end++;
        }

        return float.TryParse(line.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static Texture FindTextureForMaterial(Material sourceMaterial, string sourceName)
    {
        if (sourceMaterial != null)
        {
            string[] properties = { "_MainTex", "_BaseMap", "_BaseColorMap" };
            foreach (string property in properties)
            {
                if (sourceMaterial.HasProperty(property))
                {
                    Texture texture = sourceMaterial.GetTexture(property);
                    if (texture != null)
                    {
                        return texture;
                    }
                }
            }
        }

        string normalizedSource = NormalizeName(sourceName);
        string[] textureGuids = AssetDatabase.FindAssets("t:Texture", new[] { TextureFolder });
        Texture best = null;
        int bestScore = int.MinValue;

        foreach (string guid in textureGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string textureName = Path.GetFileNameWithoutExtension(path);
            string normalizedTexture = NormalizeName(textureName);
            int score = 0;

            if (normalizedTexture == normalizedSource)
            {
                score = 100;
            }
            else if (normalizedSource.Contains(normalizedTexture) || normalizedTexture.Contains(normalizedSource))
            {
                score = 50 + Mathf.Min(normalizedSource.Length, normalizedTexture.Length);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = AssetDatabase.LoadAssetAtPath<Texture>(path);
            }
        }

        return bestScore > 0 ? best : null;
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("_Desktop", string.Empty)
            .Replace("_Toon", string.Empty)
            .Replace("_Look", string.Empty)
            .Replace(".001", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Trim()
            .ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        string normalized = NormalizeName(value);
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = "material";
        }

        char[] chars = normalized.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars) + "_" + StableHash(value).ToString("x8");
    }

    private static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return hash;
        }
    }
}
