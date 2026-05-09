import json
import re
import subprocess
import sys
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
UNITY_ROOT = ROOT / "unity" / "SilverWolfPet"
ACTION_CONTROLLER = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetKawaiiActionController.cs"
PRODUCT_VALIDATOR = UNITY_ROOT / "Assets/TransparentPet/Editor/TransparentPetProductValidator.cs"
SCENE_BUILDER = UNITY_ROOT / "Assets/TransparentPet/Editor/TransparentPetSceneBuilder.cs"
CONTEXT_MENU = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetContextMenu.cs"
PLACEMENT_CONTROLLER = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetPlacementController.cs"
FREE_CAMERA = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetFreeCamera.cs"
FACE_TRACKER = UNITY_ROOT / "Assets/TransparentPet/Scripts/SceneHost/TransparentPetSceneFaceTracker.cs"
PET_STATE_CONTROLLER = UNITY_ROOT / "Assets/TransparentPet/Scripts/PetStateController.cs"
HEAD_LOOK_AT = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetHeadLookAt.cs"
EXPRESSION_CONTROLLER = UNITY_ROOT / "Assets/TransparentPet/Scripts/PetExpressionController.cs"
BLINK_CONTROLLER = UNITY_ROOT / "Assets/TransparentPet/Scripts/PetBlinkController.cs"
WORKSHOP_MANAGER = UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetWorkshopManager.cs"
FACE_TRACKING_PREFLIGHT = ROOT / "scripts/check_face_tracking_preflight.py"
PRODUCT_PREFLIGHT = ROOT / "scripts/check_product_preflight.py"
SCENE_BUILD_SCRIPT = ROOT / "scripts/build_unity_dual_product.ps1"


class UnityProductConfigTests(unittest.TestCase):
    def test_random_idle_actions_use_product_whitelist(self):
        source = ACTION_CONTROLLER.read_text(encoding="utf-8")
        self.assertIn("useProductRandomActionWhitelist = true", source)
        self.assertIn("!IsRandomActionAllowedForProduct(actionName)", source)

        whitelist_match = re.search(
            r"randomActionWhitelist\s*=\s*(?P<body>.*?);",
            source,
            re.DOTALL,
        )
        self.assertIsNotNone(whitelist_match)
        whitelist = "".join(re.findall(r'"([^"]*)"', whitelist_match.group("body")))
        allowed = {item.strip() for item in re.split(r"[,;\r\n]+", whitelist) if item.strip()}

        safe_actions = {
            "KA_Idle02_LookLeftAndRight",
            "KA_Idle08_ComeUpWithAnIdea",
            "KA_Idle16_WaveHands",
            "KA_Idle28_Laugh",
            "KA_Idle45_WaveHandSlightly",
            "KA_Idle50_StandingTalk1_1",
        }
        risky_actions = {
            "KA_Idle17_StumbleAndFall",
            "KA_Idle54_CartwheelAndBackHandspring",
            "KA_Idle55_Backflip",
            "KA_Idle56_Handstand",
            "KA_Idle57_Dance05",
            "KA_Idle58_Dance06",
        }

        self.assertTrue(safe_actions.issubset(allowed))
        self.assertTrue(risky_actions.isdisjoint(allowed))

    def test_product_validator_checks_random_whitelist(self):
        validator = PRODUCT_VALIDATOR.read_text(encoding="utf-8")
        self.assertIn("ValidateRandomActionWhitelist", validator)
        self.assertIn("KA_Idle17_StumbleAndFall", validator)
        self.assertIn("KA_Idle54_CartwheelAndBackHandspring", validator)
        self.assertIn("IsRandomActionAllowedForProduct", validator)

    def test_scene_product_has_user_save_and_build_entry(self):
        placement = PLACEMENT_CONTROLLER.read_text(encoding="utf-8")
        self.assertIn("SaveUserPlacementNow", placement)
        self.assertIn("ClearSavedPlacement", placement)
        self.assertIn("ResetToFactoryDefault", placement)
        self.assertIn("HasSavedPlacement", placement)

        camera = FREE_CAMERA.read_text(encoding="utf-8")
        self.assertIn("SaveUserCameraNow", camera)
        self.assertIn("ClearSavedCamera", camera)
        self.assertIn("ResetToFactoryDefault", camera)
        self.assertIn("HasSavedCamera", camera)

        menu = CONTEXT_MENU.read_text(encoding="utf-8")
        self.assertIn("SaveUserPlacementNow", menu)
        self.assertIn("SaveUserCameraNow", menu)
        self.assertIn("ResetToFactoryDefault", menu)

        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        self.assertIn("BuildSceneHostWindows", builder)
        self.assertIn("UrpHostBuildPath", builder)
        self.assertIn("ConfigureSceneHostPlayerSettings", builder)
        self.assertIn("new Vector2(520f, 560f)", builder)

        validator = PRODUCT_VALIDATOR.read_text(encoding="utf-8")
        self.assertIn("ValidatePlacementPersistence", validator)
        self.assertIn('StartsWith("ScenePet.", StringComparison.Ordinal)', validator)
        self.assertIn("Editor play does not overwrite user camera saves", validator)
        self.assertIn("Editor play does not overwrite user placement saves", validator)

        build_script = SCENE_BUILD_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("TransparentPetSceneBuilder.BuildSceneHostWindows", build_script)

    def test_desktop_product_defaults_to_right_side_small_window(self):
        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        controller = (
            UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentWindowController.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("window.presentationMode = TransparentWindowController.MonitorPresentationMode.SmallWindow", builder)
        self.assertIn("window.preferredMonitorIndex = 0", builder)
        self.assertIn("window.primaryRightWindowSizePixels = new Vector2Int(720, 960)", builder)
        self.assertIn('window.windowSettingsKey = "DesktopPet.Window.v1"', builder)
        self.assertIn("PlayerSettings.defaultScreenWidth = 720", builder)
        self.assertIn("PlayerSettings.defaultScreenHeight = 960", builder)
        self.assertIn("PlayerSettings.resizableWindow = true", builder)
        self.assertIn("int currentWidth = Mathf.Max(1, primaryRightWindowSizePixels.x);", controller)
        self.assertIn("int currentHeight = Mathf.Max(1, primaryRightWindowSizePixels.y);", controller)

    def test_voice_menu_can_edit_persona_and_polling_prompts(self):
        menu = CONTEXT_MENU.read_text(encoding="utf-8")
        launcher = (
            UNITY_ROOT / "Assets/TransparentPet/Scripts/TransparentPetVoiceRuntimeLauncher.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("DrawPromptSection", menu)
        self.assertIn("MenuView.Prompts", menu)
        self.assertIn("voiceLauncher.personaPrompt", menu)
        self.assertIn("voiceLauncher.companionPollingPrompt", menu)
        self.assertIn("ApplyPromptSettingsToConfig", menu)

        self.assertIn("public string personaPrompt", launcher)
        self.assertIn("public string companionPollingPrompt", launcher)
        self.assertIn("LoadPromptSettingsFromConfig", launcher)
        self.assertIn("SavePersonaPromptToMirroredConfigs", launcher)
        self.assertIn("SaveCompanionPromptToMirroredConfigs", launcher)
        self.assertIn("SpokenOutputGuardMessage", launcher)
        self.assertIn("VisualRouteTtsGuardMessage", launcher)
        self.assertIn("HardTtsOutputGuardMessage", launcher)
        self.assertIn("NonRepeatingVisualGuardMessage", launcher)
        self.assertIn("ScreenTruthfulnessGuardMessage", launcher)
        self.assertIn("ComposeCompanionVisionPrompt", launcher)
        self.assertIn("RecentContextCount", launcher)
        self.assertIn("Voice bridge dropped; restarting active screen/camera route.", launcher)

    def test_vision_voice_examples_keep_tts_and_screen_truth_guards(self):
        config_paths = [
            ROOT / "config/volc_traditional_voice_chat.example.json",
            UNITY_ROOT / "Assets/StreamingAssets/GodotFinal/config/volc_traditional_voice_chat.example.json",
        ]
        companion_paths = [
            ROOT / "config/volc_traditional_companion_polling.example.json",
            UNITY_ROOT / "Assets/StreamingAssets/GodotFinal/config/volc_traditional_companion_polling.example.json",
        ]

        for path in config_paths:
            with self.subTest(path=path):
                data = json.loads(path.read_text(encoding="utf-8"))
                companion = data["CompanionVision"]
                messages = data["StartVoiceChat"]["Config"]["LLMConfig"]["SystemMessages"]
                joined = "\n".join(messages)

                self.assertEqual(companion["RecentContextCount"], 3)
                self.assertIn("不要复用上一轮", companion["Prompt"])
                self.assertIn("确实收到清晰屏幕", companion["Prompt"])
                self.assertIn("禁止括号动作", companion["Prompt"])
                self.assertIn("语音输出硬规则", joined)
                self.assertIn("不要机械复用", joined)
                self.assertIn("确实收到清晰屏幕", joined)
                self.assertIn("不要猜 UI", joined)

        for path in companion_paths:
            with self.subTest(path=path):
                data = json.loads(path.read_text(encoding="utf-8"))
                companion = data["CompanionVision"]

                self.assertEqual(companion["RecentContextCount"], 3)
                self.assertIn("不要复用上一轮", companion["Prompt"])
                self.assertIn("确实收到清晰屏幕", companion["Prompt"])
                self.assertIn("禁止括号动作", companion["Prompt"])

    def test_face_tracking_preflight_runs_without_camera(self):
        result = subprocess.run(
            [sys.executable, str(FACE_TRACKING_PREFLIGHT), "--skip-imports"],
            cwd=ROOT,
            text=True,
            encoding="utf-8",
            errors="replace",
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=30,
            check=False,
        )

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Face tracking preflight passed", result.stdout)
        self.assertIn('"scene_has_face_tracker": true', result.stdout)
        self.assertIn('"voice_uses_camera_hub_default": true', result.stdout)

    def test_scene_face_tracking_does_not_recenter_manual_camera(self):
        tracker = FACE_TRACKER.read_text(encoding="utf-8")
        self.assertNotIn("freeCamera.SetFollowPlacementTarget(true);", tracker)
        self.assertIn("trackingEnabled = startCameraOnEnable || settings.trackingEnabled", tracker)
        self.assertIn("trackingEnabled = startCameraOnEnable || trackingEnabled", tracker)

    def test_scene_camera_locks_focus_and_depth_of_field_by_default(self):
        camera = FREE_CAMERA.read_text(encoding="utf-8")
        self.assertIn("CurrentCameraStateVersion = 5", camera)
        self.assertIn("MinimumCameraDistanceMeters = 0.15f", camera)
        self.assertIn("ClampExternalCameraOffset", camera)
        self.assertIn("public bool depthOfFieldEnabled = true", camera)
        self.assertIn("state.version >= 4 ? state.depthOfFieldEnabled : true", camera)
        self.assertIn("bool keepPlacementTargetLocked", camera)
        self.assertIn("followPlacementTarget = keepPlacementTargetLocked", camera)

        placement = PLACEMENT_CONTROLLER.read_text(encoding="utf-8")
        self.assertIn("public bool lockCameraTargetToPet = true", placement)
        self.assertIn("CameraTargetLockedToPet", placement)
        self.assertIn("SetCameraTargetLockedToPet", placement)
        self.assertIn("freeCamera.SetFollowPlacementTarget(lockCameraTargetToPet)", placement)
        self.assertIn("lockCameraTargetToPet && !freeCamera.followPlacementTarget", placement)
        self.assertIn("version = 5", placement)
        self.assertIn("lockCameraTargetToPet = lockCameraTargetToPet", placement)
        self.assertIn("state.version >= 5 ? state.lockCameraTargetToPet : true", placement)

        menu = CONTEXT_MENU.read_text(encoding="utf-8")
        self.assertIn("CameraTargetLockedToPet", menu)
        self.assertIn("SetCameraTargetLockedToPet", menu)

        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        self.assertIn("freeCamera.depthOfFieldEnabled = true", builder)
        self.assertIn("freeCamera.lockDepthOfFieldToPet = true", builder)
        self.assertIn("placementController.lockCameraTargetToPet = true", builder)

        validator = PRODUCT_VALIDATOR.read_text(encoding="utf-8")
        self.assertIn("Depth of field is enabled by default", validator)
        self.assertIn("Camera focus stays locked to the pet while placement changes", validator)

        scene = (UNITY_ROOT / "Assets/Scenes/BlenderIndoorScene.unity").read_text(encoding="utf-8")
        self.assertIn("lockCameraTargetToPet: 1", scene)
        self.assertIn("depthOfFieldEnabled: 1", scene)

    def test_scene_face_tracking_uses_stable_jitter_defaults(self):
        tracker = FACE_TRACKER.read_text(encoding="utf-8")
        self.assertIn("CurrentSettingsVersion = 19", tracker)
        self.assertIn("StableTrackingDefaultsSettingsVersion = 7", tracker)
        self.assertIn("GlobalTrackingSensitivitySettingsVersion = 10", tracker)
        self.assertIn("GlobalTrackingHeightDepthSettingsVersion = 12", tracker)
        self.assertIn("GlobalTrackingAxisFixSettingsVersion = 13", tracker)
        self.assertIn("GlobalTrackingLateralRangeSettingsVersion = 14", tracker)
        self.assertIn("GlobalTrackingLateralBalanceSettingsVersion = 17", tracker)
        self.assertIn("StableNormalizedDeadZone = 0.07f", tracker)
        self.assertIn("StableNormalizedDepthDeadZone = 0.05f", tracker)
        self.assertIn("StableOffsetSmoothTime = 0.3f", tracker)
        self.assertIn("StableDepthSmoothTime = 0.32f", tracker)
        self.assertIn("StableCameraTargetShiftMeters = 0.08f", tracker)
        self.assertIn("StableCameraDepthShiftMeters = 0.06f", tracker)
        self.assertIn("StableCameraHeightFollowMeters = 0.55f", tracker)
        self.assertIn("StableCameraOrbitDeadZoneDegrees = 5f", tracker)
        self.assertIn("StableCameraOrbitSmoothTime = 0.32f", tracker)
        self.assertIn("GlobalTrackingWideAngleBalanceSettingsVersion = 19", tracker)
        self.assertIn("StableGlobalTrackingLateralMeters = 0.18f", tracker)
        self.assertIn("StableCameraYawOrbitStrength = 1.0f", tracker)
        self.assertIn("StableGlobalTrackingHeightMeters = 0.225f", tracker)
        self.assertIn("StableGlobalTrackingDepthMeters = 0.1375f", tracker)
        self.assertIn("StableGlobalTrackingOffsetSmoothTime = 0.2f", tracker)
        self.assertIn("StableGlobalTrackingDepthSmoothTime = 0.22f", tracker)
        self.assertIn("ExternalJumpOffsetThreshold = 0.45f", tracker)
        self.assertIn("ExternalJumpConfirmPackets = 3", tracker)
        self.assertIn("ShouldHoldExternalJump", tracker)
        self.assertIn("jump held", tracker)
        self.assertIn("GlobalTrackingV8LateralMigrationScale = 0.375f", tracker)
        self.assertIn("GlobalTrackingV8HeightDepthMigrationScale = 0.25f", tracker)
        self.assertIn("GlobalTrackingV9LateralMigrationScale = 0.75f", tracker)
        self.assertIn("GlobalTrackingV9HeightDepthMigrationScale = 0.5f", tracker)
        self.assertIn("GlobalTrackingV11HeightDepthMigrationScale = 0.5f", tracker)
        self.assertIn("globalTrackingLateralMeters = StableGlobalTrackingLateralMeters", tracker)
        self.assertIn("StableHeadYawPoseWeight = 0.22f", tracker)
        self.assertIn("StableHeadPitchPoseWeight = 0.18f", tracker)
        self.assertIn("ApplyStableTrackingDefaults", tracker)
        self.assertIn("settings.settingsVersion < StableTrackingDefaultsSettingsVersion", tracker)
        self.assertIn("cameraOrbitDeadZoneDegrees = cameraOrbitDeadZoneDegrees", tracker)
        self.assertIn("cameraHeightFollowMeters = cameraHeightFollowMeters", tracker)
        self.assertIn("-Vector3.up * (_smoothOffset.y * cameraHeightFollowMeters)", tracker)
        self.assertIn("freeCamera.SetExternalTargetOffset(heightTargetOffset)", tracker)

        scene = (UNITY_ROOT / "Assets/Scenes/BlenderIndoorScene.unity").read_text(encoding="utf-8")
        self.assertIn("normalizedDeadZone: 0.07", scene)
        self.assertIn("normalizedDepthDeadZone: 0.05", scene)
        self.assertIn("offsetSmoothTime: 0.3", scene)
        self.assertIn("depthSmoothTime: 0.32", scene)
        self.assertIn("cameraTargetShiftMeters: 0.08", scene)
        self.assertIn("cameraDepthShiftMeters: 0.06", scene)
        self.assertIn("cameraHeightFollowMeters: 0.55", scene)
        self.assertIn("cameraOrbitDeadZoneDegrees: 5", scene)
        self.assertIn("cameraOrbitSmoothTime: 0.32", scene)

        head_tracker = (ROOT / "head_tracker/head_tracker.py").read_text(encoding="utf-8")
        self.assertIn('parser.add_argument("--cutoff-hz", type=float, default=4.0)', head_tracker)
        self.assertIn('parser.add_argument("--deadzone-xy", type=float, default=0.04)', head_tracker)
        self.assertIn('parser.add_argument("--deadzone-z", type=float, default=0.035)', head_tracker)

    def test_scene_face_tracking_has_optional_global_tracking_mode(self):
        tracker = FACE_TRACKER.read_text(encoding="utf-8")
        self.assertIn("public bool globalTrackingEnabled", tracker)
        self.assertIn("public bool GlobalTrackingEnabled => globalTrackingEnabled", tracker)
        self.assertIn("SetGlobalTrackingEnabled", tracker)
        self.assertIn("BuildGlobalTrackingCameraOffset", tracker)
        self.assertIn("BuildGlobalTrackingTargetOffset", tracker)
        self.assertIn("freeCamera.SetExternalTargetOffset(targetOffset)", tracker)
        self.assertIn("return targetCamera.transform.forward * (_smoothDepthOffset * globalTrackingDepthMeters)", tracker)
        self.assertIn("activeOffsetSmoothTime = globalTrackingEnabled ? globalTrackingOffsetSmoothTime : offsetSmoothTime", tracker)
        self.assertIn("activeDepthSmoothTime = globalTrackingEnabled ? globalTrackingDepthSmoothTime : depthSmoothTime", tracker)
        self.assertIn("if (!cameraParallaxEnabled || freeCamera == null || targetCamera == null)", tracker)
        self.assertIn("heightAxis = mirrorVertical ? -Vector3.up : Vector3.up", tracker)
        self.assertIn("heightAxis * (_smoothOffset.y * globalTrackingHeightMeters)", tracker)
        self.assertIn("fromFirstGlobalTrackingTuning", tracker)
        self.assertIn("settings.settingsVersion >= 8 && settings.settingsVersion < GlobalTrackingSensitivitySettingsVersion", tracker)
        self.assertIn("globalTrackingEnabled = true", tracker)
        self.assertIn("globalTrackingEnabled = globalTrackingEnabled", tracker)

        menu = CONTEXT_MENU.read_text(encoding="utf-8")
        self.assertNotIn("DrawToggle(\"\\u5168\\u5c40\\u8ddf\\u8e2a\"", menu)
        self.assertIn("SceneCameraHubOwnsCamera", menu)
        self.assertIn("\\u955c\\u5934\\u9501\\u5b9a\\u4eba\\u7269", menu)
        self.assertIn("\\u5de6\\u53f3\\u5e73\\u79fb\\u5f3a\\u5ea6", menu)
        self.assertIn("\\u5de6\\u53f3\\u65cb\\u8f6c\\u5f3a\\u5ea6", menu)
        self.assertIn("SetGlobalTrackingLateralMeters", menu)
        self.assertIn("SetGlobalTrackingHeightMeters", menu)
        self.assertIn("SetGlobalTrackingDepthMeters", menu)

        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        self.assertIn("sceneFaceTracker.globalTrackingEnabled = true", builder)
        self.assertIn("sceneFaceTracker.globalTrackingLateralMeters = 0.18f", builder)
        self.assertIn("sceneFaceTracker.globalTrackingHeightMeters = 0.225f", builder)
        self.assertIn("sceneFaceTracker.globalTrackingDepthMeters = 0.1375f", builder)
        self.assertIn("sceneFaceTracker.globalTrackingOffsetSmoothTime = 0.2f", builder)
        self.assertIn("sceneFaceTracker.globalTrackingDepthSmoothTime = 0.22f", builder)

        scene = (UNITY_ROOT / "Assets/Scenes/BlenderIndoorScene.unity").read_text(encoding="utf-8")
        self.assertIn("globalTrackingEnabled: 1", scene)
        self.assertIn("globalTrackingLateralMeters: 0.18", scene)
        self.assertIn("cameraYawOrbitStrength: 1", scene)
        self.assertIn("globalTrackingHeightMeters: 0.225", scene)
        self.assertIn("globalTrackingDepthMeters: 0.1375", scene)
        self.assertIn("globalTrackingOffsetSmoothTime: 0.2", scene)
        self.assertIn("globalTrackingDepthSmoothTime: 0.22", scene)

    def test_speech_display_strips_parenthesized_stage_directions(self):
        controller = PET_STATE_CONTROLLER.read_text(encoding="utf-8")
        self.assertIn("SanitizeSpeechDisplayText(rawBubbleText)", controller)
        self.assertIn("StripBracketedSpeechHints", controller)
        self.assertIn("case '(':", controller)
        self.assertIn("case '\\uFF08':", controller)
        self.assertIn("mouthController.QueueMouthText(bubbleText)", controller)
        self.assertIn("screenSubtitleController.ShowSubtitle(bubbleText, visibleSeconds)", controller)

    def test_product_preflight_checks_mouth_expression_map(self):
        source = PRODUCT_PREFLIGHT.read_text(encoding="utf-8")
        self.assertIn("check_expression_map", source)
        self.assertIn("mouth_round", source)
        self.assertIn("mouth_smirk", source)

        root_map = json.loads((ROOT / "config/expression_map.json").read_text(encoding="utf-8-sig"))
        scene_map = json.loads(
            (
                UNITY_ROOT
                / "Assets/StreamingAssets/GodotFinal/config/expression_map.json"
            ).read_text(encoding="utf-8-sig")
        )
        self.assertEqual(root_map, scene_map)

        mouth_expressions = root_map["expressions"]
        required = {
            "mouth_open",
            "mouth_small",
            "mouth_wide",
            "mouth_round",
            "mouth_closed",
            "mouth_smirk",
        }
        self.assertTrue(required.issubset(mouth_expressions))
        for key in required:
            self.assertEqual("mouth", mouth_expressions[key]["exclusive_group"])
            self.assertTrue(mouth_expressions[key]["blend_shapes"])

    def test_workshop_mods_runtime_scans_and_persists_packages(self):
        manager = WORKSHOP_MANAGER.read_text(encoding="utf-8")
        menu = CONTEXT_MENU.read_text(encoding="utf-8")
        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        agents = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
        workshop_doc = (ROOT / "docs/WORKSHOP_PACKAGE_FORMAT.md").read_text(encoding="utf-8")

        self.assertIn("manifest.json", manager)
        self.assertIn("persistentWorkshopFolderName = \"Workshop\"", manager)
        self.assertIn("streamingWorkshopFolderName = \"Workshop\"", manager)
        self.assertIn("voicechatpet.Workshop.", manager)
        self.assertIn("SelectedModel.v1", manager)
        self.assertIn("AssetBundle.LoadFromFile", manager)
        self.assertIn("RebindModelRoot", manager)
        self.assertIn("headLookAt.Rebind", manager)
        self.assertIn("Runtime importer is not installed", manager)
        self.assertIn("FBX is creator-source input", manager)

        head_look_at = HEAD_LOOK_AT.read_text(encoding="utf-8")
        self.assertIn("public void Rebind", head_look_at)
        self.assertIn("_head = null", head_look_at)
        self.assertIn("_neck = null", head_look_at)

        self.assertIn("MenuView.Workshop", menu)
        self.assertIn("DrawWorkshopSection", menu)
        self.assertIn("OpenUserWorkshopFolder", menu)

        self.assertIn("TransparentPetWorkshopManager workshopManager = root.AddComponent<TransparentPetWorkshopManager>()", builder)
        self.assertIn("contextMenu.workshopManager = workshopManager", builder)

        self.assertIn("Steam Workshop / Mods", readme)
        self.assertIn("docs/WORKSHOP_PACKAGE_FORMAT.md", readme)
        self.assertIn("FBX is creator-source input", agents)
        self.assertIn("Runtime Workshop items should be ready-to-scan packages", agents)
        self.assertIn("The current first pass does not load `.fbx` at runtime", workshop_doc)

    def test_manual_camera_offset_survives_pet_focus_lock(self):
        camera = FREE_CAMERA.read_text(encoding="utf-8")
        placement = PLACEMENT_CONTROLLER.read_text(encoding="utf-8")

        self.assertIn("CurrentCameraStateVersion = 5", camera)
        self.assertIn("private Vector3 _manualTargetOffset", camera)
        self.assertIn("public Vector3 ManualTargetOffset", camera)
        self.assertIn("manualTargetOffset = _manualTargetOffset", camera)
        self.assertIn("state.version >= 5 ? state.manualTargetOffset : Vector3.zero", camera)
        self.assertIn("private void PanWorld", camera)
        self.assertIn("_manualTargetOffset += worldDelta", camera)
        self.assertNotIn("followPlacementTarget = false;\n        target += cameraTransform.right", camera)

        self.assertIn("freeCamera.SetExternalTarget(focusPoint)", placement)

    def test_pet_blink_controller_is_wired_for_placeholder_and_custom_models(self):
        blink = BLINK_CONTROLLER.read_text(encoding="utf-8")
        expression = EXPRESSION_CONTROLLER.read_text(encoding="utf-8")
        builder = SCENE_BUILDER.read_text(encoding="utf-8")
        workshop = WORKSHOP_MANAGER.read_text(encoding="utf-8")

        self.assertIn("public sealed class PetBlinkController", blink)
        self.assertIn("blinkExpressionName = \"blink\"", blink)
        self.assertIn("FindChildByName(scanRoot, \"LeftEye\")", blink)
        self.assertIn("FindChildByName(scanRoot, \"RightEye\")", blink)
        self.assertIn("expressionController.SetExpressionWeight(blinkExpressionName, weight)", blink)

        self.assertIn("public bool HasExpressionTargets", expression)
        self.assertIn("public void RebindScanRoot", expression)
        self.assertIn('case "blink":', expression)
        self.assertIn('fallbackCategory != "blink"', expression)

        self.assertIn("PetBlinkController blinkController = petBody.AddComponent<PetBlinkController>()", builder)
        self.assertIn("blinkController.expressionController = expressionController", builder)

        self.assertIn("public PetBlinkController blinkController", workshop)
        self.assertIn("blinkController.Rebind(newRoot)", workshop)

        root_map = json.loads((ROOT / "config/expression_map.json").read_text(encoding="utf-8-sig"))
        scene_map = json.loads(
            (
                UNITY_ROOT
                / "Assets/StreamingAssets/GodotFinal/config/expression_map.json"
            ).read_text(encoding="utf-8-sig")
        )
        self.assertEqual(root_map, scene_map)
        blink_expression = root_map["expressions"]["blink"]
        self.assertEqual("blink", blink_expression["exclusive_group"])
        self.assertFalse(blink_expression["reset_others"])
        self.assertTrue(blink_expression["blend_shapes"])


if __name__ == "__main__":
    unittest.main()
