import VERTC, {
  MediaType,
  StreamIndex,
  SUBTITLE_MODE,
  VideoSourceType
} from "/vendor/volcengine-rtc.esm.min.js";
import { FaceLandmarker, FilesetResolver } from "/vendor/mediapipe/tasks-vision.mjs";

const DEFAULT_CAPTURE_VOLUME = 150;

const state = {
  config: null,
  engine: null,
  joined: false,
  voiceStarted: false,
  hasStartedVoiceOnce: false,
  audioPublished: false,
  screenPublished: false,
  screenStream: null,
  screenTrack: null,
  screenVideo: null,
  screenCompositeCanvas: null,
  screenCompositeContext: null,
  screenCompositeTrack: null,
  screenCompositeAnimationFrame: 0,
  screenCompositeLastDrawAt: 0,
  screenCompositeFrameCounter: 0,
  screenCameraOverlayImage: null,
  screenCameraOverlaySnapshotUrl: "",
  screenCameraOverlaySnapshotLoading: false,
  screenCameraOverlayLastSnapshotErrorAt: 0,
  screenCameraOverlayObjectUrl: "",
  screenUsingExternalTrack: false,
  visionCompositeOnMain: false,
  cameraDesired: false,
  cameraPublished: false,
  cameraStream: null,
  cameraTrack: null,
  cameraUsingExternalTrack: false,
  cameraVideo: null,
  cameraHubImage: null,
  cameraHubCanvas: null,
  cameraHubContext: null,
  cameraHubStream: null,
  cameraHubTrack: null,
  cameraHubAnimationFrame: 0,
  cameraHubLastDrawAt: 0,
  cameraHubSnapshotUrl: "",
  cameraHubSnapshotLoading: false,
  cameraHubLastSnapshotErrorAt: 0,
  cameraHubSessionId: 0,
  cameraHubObjectUrl: "",
  cameraNextRetryAt: 0,
  cameraRequestedDeviceLabel: "",
  faceLandmarker: null,
  faceLandmarkerDelegate: "GPU",
  faceTrackingActive: false,
  faceTrackingTimer: 0,
  faceTrackingInFlight: false,
  faceTrackingLastVideoTime: -1,
  faceTrackingLastPacketAt: 0,
  faceTrackingPacketLogCount: 0,
  subtitleStarted: false,
  remoteBotAudioSubscriptions: new Set(),
  microphoneCaptureVolume: DEFAULT_CAPTURE_VOLUME,
  microphoneEchoMuted: false,
  microphoneEchoGuardVolume: DEFAULT_CAPTURE_VOLUME,
  microphoneEchoUnmuteTimer: 0,
  microphoneMuteReasons: new Set(),
  microphoneGuardVolumes: new Map(),
  visionDesired: false,
  visionSupported: false,
  s2sVisionSupported: false,
  visionChanging: false,
  cameraChanging: false,
  visionPollTimer: 0,
  cameraPollTimer: 0,
  voiceOutputPollTimer: 0,
  externalTextPollTimer: 0,
  eventForwardingActive: false,
  lastStopAt: 0,
  startVisionWithSession: false,
  starting: false,
  stopping: false
};

const el = {
  status: document.querySelector("#status"),
  room: document.querySelector("#room"),
  user: document.querySelector("#user"),
  bot: document.querySelector("#bot"),
  log: document.querySelector("#log"),
  start: document.querySelector("#start"),
  stop: document.querySelector("#stop"),
  visionStart: document.querySelector("#visionStart"),
  visionStop: document.querySelector("#visionStop"),
  cameraStart: document.querySelector("#cameraStart"),
  cameraStop: document.querySelector("#cameraStop"),
  check: document.querySelector("#check"),
  clientSubtitle: document.querySelector("#clientSubtitle")
};

boot();
window.silverWolfVoiceRuntimeStart = startSession;
window.silverWolfVoiceRuntimeStop = stopSession;
window.silverWolfVoiceRuntimeStartVision = requestVisionStart;
window.silverWolfVoiceRuntimeStopVision = requestVisionStop;
window.addEventListener("beforeunload", stopSessionBeacon);

async function boot() {
  try {
    const response = await fetchJson("/api/config");
    state.config = response.config;
    el.room.textContent = state.config.rtc.roomId || "-";
    el.user.textContent = state.config.rtc.userId || "-";
    el.bot.textContent = state.config.rtc.botUid || "-";
    if (state.config.rtc.enableClientSubtitle === false) {
      el.clientSubtitle.checked = false;
    }
    log("Config loaded. RTC SDK: " + safeSdkVersion(), "ok");
    startVisionPolling();
    startCameraPolling();
    startVoiceOutputPolling();
    startExternalTextPolling();
    const params = new URLSearchParams(window.location.search);
    state.startVisionWithSession = params.get("vision") === "1";
    if (params.get("autostart") === "1") {
      window.setTimeout(async () => {
        await startSession();
      }, 350);
    }
  } catch (error) {
    log("Config load failed: " + error.message, "bad");
  }
}

el.start.addEventListener("click", () => startSession());
el.stop.addEventListener("click", () => stopSession());
el.visionStart.addEventListener("click", () => requestVisionStart());
el.visionStop.addEventListener("click", () => requestVisionStop());
el.cameraStart.addEventListener("click", () => requestCameraStart());
el.cameraStop.addEventListener("click", () => requestCameraStop());
el.check.addEventListener("click", () => checkConfig());

async function startSession() {
  if (state.starting || state.voiceStarted) {
    log("Start ignored: session is already starting or running.", "warn");
    return;
  }
  if (!state.config) {
    log("Config is not loaded yet.", "bad");
    return;
  }
  if (!state.config.rtc.token) {
    log("RTC token is empty. Regenerate local config first.", "bad");
    return;
  }

  state.starting = true;
  setBusy(true);
  try {
    const rtc = state.config.rtc;
    const engine = VERTC.createEngine(rtc.appId);
    state.engine = engine;
    bindEngineEvents(engine);

    setStatus("Joining RTC room...", "warn");
    log("Joining RTC room " + rtc.roomId + " as " + rtc.userId);
    await engine.joinRoom(
      rtc.token,
      rtc.roomId,
      {
        userId: rtc.userId,
        extraInfo: JSON.stringify({ source_language: "zh" })
      },
      {
        isAutoPublish: false,
        isAutoSubscribeAudio: false,
        isAutoSubscribeVideo: false
      }
    );
    state.joined = true;
    log("RTC room joined.", "ok");

    await startMicrophoneBestEffort(engine, rtc);
    await startSubtitleBestEffort(engine, "before StartVoiceChat");
    if (state.startVisionWithSession) {
      const status = await fetchJson("/api/vision/start", { method: "POST" });
      state.visionDesired = Boolean(status.desired);
      state.visionSupported = status.visionSupported !== false;
      state.s2sVisionSupported = status.s2sVisionSupported !== false;
      if (state.visionSupported) {
        await startScreenVisionBeforeVoiceBestEffort();
        if (!state.screenPublished) {
          markVisionNeedsUserGesture();
        }
      } else {
        log(status.message || "This voice route does not support direct screen vision.", "warn");
      }
    }

    const suppressWelcome = Boolean(state.hasStartedVoiceOnce || state.voiceStarted);
    setStatus("Starting AI voice task...", "warn");
    log("Calling StartVoiceChat after local audio publish." + (suppressWelcome ? " Welcome suppressed for restart." : " Welcome message should play after bot joins."));
    await fetchJson("/api/start_voice_chat", {
      method: "POST",
      body: JSON.stringify({ forceRestart: true, suppressWelcome })
    });
    await startSubtitleBestEffort(engine, "after StartVoiceChat");
    window.setTimeout(() => startSubtitleBestEffort(engine, "delayed retry"), 1800);
    state.voiceStarted = true;
    state.hasStartedVoiceOnce = true;
    state.eventForwardingActive = true;
    el.stop.disabled = false;
    setStatus("AI voice task started.", "ok");
    log("StartVoiceChat started.", "ok");
  } catch (error) {
    log("Start failed: " + error.message, "bad");
    await stopSession();
  } finally {
    state.starting = false;
    setBusy(false);
  }
}

async function startMicrophoneBestEffort(engine, rtc) {
  try {
    setStatus("Starting microphone...", "warn");
    if (typeof engine.setAudioCaptureConfig === "function") {
      try {
        await engine.setAudioCaptureConfig({
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true
        });
        log("Microphone capture config enabled: echo cancellation / noise suppression / auto gain.", "ok");
      } catch (configError) {
        log("Microphone capture config ignored: " + configError.message, "warn");
      }
    }
    await engine.startAudioCapture();
    boostCaptureVolumeBestEffort(engine, rtc);
    await engine.publishStream(MediaType.AUDIO);
    state.audioPublished = true;
    engine.enableAudioPropertiesReport({
      interval: rtc.audioReportIntervalMs || 100,
      enableInBackground: true
    });
    setStatus("Voice session running.", "ok");
    log("Microphone published. Remote audio volume report enabled.", "ok");
  } catch (error) {
    setStatus("AI started, microphone failed.", "warn");
    log("Microphone publish failed, but StartVoiceChat remains running: " + error.message, "warn");
  }
}

function boostCaptureVolumeBestEffort(engine, rtc) {
  const configured = Number(rtc.captureVolume ?? rtc.captureVolumePercent ?? DEFAULT_CAPTURE_VOLUME);
  const captureVolume = Number.isFinite(configured)
    ? Math.max(0, Math.min(400, Math.round(configured)))
    : DEFAULT_CAPTURE_VOLUME;
  state.microphoneCaptureVolume = captureVolume;
  if (captureVolume === 100 || typeof engine.setCaptureVolume !== "function") {
    return captureVolume;
  }
  try {
    engine.setCaptureVolume(StreamIndex.STREAM_INDEX_MAIN, captureVolume);
    log("Microphone capture volume set to " + captureVolume + ".", "ok");
  } catch (error) {
    log("Microphone capture volume ignored: " + error.message, "warn");
  }
  return captureVolume;
}

function startVisionPolling() {
  if (state.visionPollTimer) {
    return;
  }
  state.visionPollTimer = window.setInterval(() => pollVisionStatus(), 1000);
  pollVisionStatus();
}

function startExternalTextPolling() {
  if (state.externalTextPollTimer) {
    return;
  }
  state.externalTextPollTimer = window.setInterval(() => pollExternalTextToLlm(), 350);
  pollExternalTextToLlm();
}

async function pollExternalTextToLlm() {
  try {
    const data = await fetchJson("/api/external_text_to_llm/pending");
    const messages = Array.isArray(data.messages) ? data.messages : [];
    for (const item of messages) {
      await sendExternalTextToLlm(item);
    }
  } catch (error) {
    log("ExternalTextToLLM poll failed: " + error.message, "warn");
  }
}

async function sendExternalTextToLlm(item) {
  const botUid = item.botUid || state.config?.rtc?.botUid || "";
  const id = item.id;
  try {
    if (!state.engine || !state.joined) {
      throw new Error("RTC room is not joined");
    }
    if (!botUid) {
      throw new Error("bot uid is empty");
    }
    const bodyPayload = {
      Command: "ExternalTextToLLM",
      Message: normalizeExternalTextToLlmMessage(item.text || ""),
      InterruptMode: clampInteger(item.interruptMode, 3, 1, 3)
    };
    if (item.imageConfig && typeof item.imageConfig === "object") {
      bodyPayload.ImageConfig = item.imageConfig;
    }
    const body = JSON.stringify(bodyPayload);
    const buffer = stringToTlv(body, "ctrl");
    await state.engine.sendUserBinaryMessage(botUid, buffer);
    log("ExternalTextToLLM sent #" + id + " -> " + botUid, "ok");
    await fetchJson("/api/external_text_to_llm/result", {
      method: "POST",
      body: JSON.stringify({
        id,
        ok: true,
        botUid,
        transport: "web_binary",
        source: item.source || "",
        messageType: item.messageType || ""
      })
    });
  } catch (error) {
    log("ExternalTextToLLM failed #" + id + ": " + error.message, "bad");
    await fetchJson("/api/external_text_to_llm/result", {
      method: "POST",
      body: JSON.stringify({ id, ok: false, botUid, error: error.message })
    }).catch(() => null);
  }
}

function normalizeExternalTextToLlmMessage(text) {
  let value = String(text || "").replace(/\s+/g, " ").trim();
  if (!value) {
    return "请简单回应一下。";
  }
  if (!/[。！？!?；;,.，]$/.test(value)) {
    value += "。";
  }
  if (value.length <= 200) {
    return value;
  }
  value = value.slice(0, 199).replace(/[，,、；;：:\s]+$/g, "");
  return `${value}。`.slice(0, 200);
}

function stringToTlv(inputString, type = "") {
  const typeBytes = new Uint8Array(4);
  for (let i = 0; i < Math.min(4, type.length); i += 1) {
    typeBytes[i] = type.charCodeAt(i);
  }
  const valueBytes = new TextEncoder().encode(inputString);
  const tlvBytes = new Uint8Array(typeBytes.length + 4 + valueBytes.length);
  tlvBytes.set(typeBytes, 0);
  const length = valueBytes.length;
  tlvBytes[4] = (length >> 24) & 0xff;
  tlvBytes[5] = (length >> 16) & 0xff;
  tlvBytes[6] = (length >> 8) & 0xff;
  tlvBytes[7] = length & 0xff;
  tlvBytes.set(valueBytes, 8);
  return tlvBytes.buffer;
}

async function pollVisionStatus() {
  try {
    const status = await fetchJson("/api/vision/status");
    state.visionDesired = Boolean(status.desired);
    state.visionSupported = status.visionSupported !== false;
    state.s2sVisionSupported = status.s2sVisionSupported !== false;
    const settingsChanged = updateRuntimeSettings("screenVision", status.settings);
    updateVisionButtons();
    if (!state.visionSupported) {
      if (state.screenPublished) {
        await stopScreenVisionBestEffort("vision unsupported");
      }
      return;
    }
    if (settingsChanged && state.visionDesired && state.screenPublished) {
      await stopScreenVisionBestEffort("bridge settings changed");
      await startScreenVisionBestEffort("bridge settings changed");
      return;
    }
    if (state.visionDesired && !state.screenPublished) {
      await startScreenVisionBestEffort("bridge desired");
    } else if (!state.visionDesired && state.screenPublished) {
      await stopScreenVisionBestEffort("bridge desired off");
    }
  } catch (error) {
    log("Vision status poll failed: " + error.message, "warn");
  }
}

async function requestVisionStart() {
  try {
    state.visionDesired = true;
    const status = await fetchJson("/api/vision/start", { method: "POST" });
    state.visionDesired = Boolean(status.desired);
    state.visionSupported = status.visionSupported !== false;
    state.s2sVisionSupported = status.s2sVisionSupported !== false;
    updateVisionButtons();
    if (state.visionSupported) {
      await startScreenVisionBestEffort("manual click");
    }
    if (!state.visionSupported) {
      log(status.message || "This voice route does not support direct screen vision.", "warn");
    } else {
      log("Screen vision requested. Choose the game/window/screen in the picker.", "ok");
    }
    await pollVisionStatus();
  } catch (error) {
    log("Screen vision request failed: " + error.message, "bad");
  }
}

async function requestVisionStop() {
  try {
    await fetchJson("/api/vision/stop", { method: "POST" });
    state.visionDesired = false;
    log("Screen vision stop requested.", "warn");
    await pollVisionStatus();
  } catch (error) {
    log("Screen vision stop failed: " + error.message, "bad");
  }
}

function startCameraPolling() {
  if (state.cameraPollTimer) {
    return;
  }
  state.cameraPollTimer = window.setInterval(() => pollCameraStatus(), 1000);
  pollCameraStatus();
}

function startVoiceOutputPolling() {
  if (state.voiceOutputPollTimer) {
    return;
  }
  state.voiceOutputPollTimer = window.setInterval(() => pollVoiceOutputStatus(), 250);
  pollVoiceOutputStatus();
}

async function pollVoiceOutputStatus() {
  try {
    const status = await fetchJson("/api/voice_output/status");
    updateRuntimeSettings("voiceOutput", {
      provider: status.provider,
      effectiveProvider: status.effectiveProvider,
      localTtsActive: Boolean(status.localTtsActive),
      muteVolcRemoteAiAudio: Boolean(status.muteVolcRemoteAiAudio),
      muteMicrophoneDuringLocalTts: Boolean(status.muteMicrophoneDuringLocalTts),
      muteMicrophoneDuringRemoteAiAudio: status.muteMicrophoneDuringRemoteAiAudio !== false,
      remoteAiAudioEchoGuardReleaseMs: status.remoteAiAudioEchoGuardReleaseMs,
      remoteAiAudioEchoGuardCaptureVolume: status.remoteAiAudioEchoGuardCaptureVolume
    });
    if (shouldMuteVolcRemoteAiAudio()) {
      await stopSubscribedBotAudio("voice output mute");
    }
    updateLocalTtsEchoGuard(status);
  } catch (error) {
    log("Voice output status poll failed: " + error.message, "warn");
  }
}

function updateLocalTtsEchoGuard(status) {
  if (!state.engine || typeof state.engine.setCaptureVolume !== "function" || !state.audioPublished) {
    return;
  }
  const settings = state.config?.voiceOutput || {};
  const enabled = settings.muteMicrophoneDuringLocalTts !== false;
  const shouldMute = Boolean(enabled && status?.localTtsActive && status?.state === "speaking");
  const holdMs = clampInteger(settings.microphoneEchoGuardReleaseMs, 650, 0, 3000);
  setMicrophoneEchoGuard("local_tts", shouldMute, holdMs, "local TTS echo guard", 0);
}

function updateRemoteAiAudioEchoGuard(report) {
  if (!state.engine || typeof state.engine.setCaptureVolume !== "function" || !state.audioPublished) {
    return;
  }
  const settings = state.config?.voiceOutput || {};
  if (settings.muteMicrophoneDuringRemoteAiAudio === false) {
    return;
  }
  const botUid = state.config?.rtc?.botUid || "";
  if (!botUid) {
    return;
  }
  const active = iterAudioReportItems(report).some(item => {
    const uid = readAudioReportUserId(item);
    return uid === botUid && readAudioReportVolume01(item) >= 0.018;
  });
  const holdMs = clampInteger(settings.remoteAiAudioEchoGuardReleaseMs, 1800, 600, 6000);
  const duckVolume = clampInteger(
    settings.remoteAiAudioEchoGuardCaptureVolume,
    0,
    0,
    Math.max(0, state.microphoneCaptureVolume)
  );
  setMicrophoneEchoGuard("remote_ai_audio", active, holdMs, "remote AI audio echo guard", duckVolume);
}

function setMicrophoneEchoGuard(reasonKey, shouldMute, holdMs, label, targetVolume = 0) {
  if (!state.engine || typeof state.engine.setCaptureVolume !== "function" || !state.audioPublished) {
    return;
  }
  if (shouldMute) {
    if (state.microphoneEchoUnmuteTimer) {
      window.clearTimeout(state.microphoneEchoUnmuteTimer);
      state.microphoneEchoUnmuteTimer = 0;
    }
    state.microphoneMuteReasons.add(reasonKey);
    state.microphoneGuardVolumes.set(reasonKey, clampInteger(targetVolume, 0, 0, Math.max(0, state.microphoneCaptureVolume)));
    const guardVolume = readMicrophoneGuardVolume();
    if (!state.microphoneEchoMuted || state.microphoneEchoGuardVolume !== guardVolume) {
      setMicrophoneCaptureVolumeBestEffort(guardVolume, label);
      state.microphoneEchoMuted = true;
      state.microphoneEchoGuardVolume = guardVolume;
      const mode = guardVolume <= 0 ? "muted" : "ducked to " + guardVolume;
      log("Microphone capture " + mode + " while AI audio is playing (" + reasonKey + ").", "ok");
    }
    return;
  }
  state.microphoneMuteReasons.delete(reasonKey);
  state.microphoneGuardVolumes.delete(reasonKey);
  if (!state.microphoneEchoMuted) {
    return;
  }
  if (state.microphoneMuteReasons.size > 0) {
    const guardVolume = readMicrophoneGuardVolume();
    if (state.microphoneEchoGuardVolume !== guardVolume) {
      setMicrophoneCaptureVolumeBestEffort(guardVolume, label + " handoff");
      state.microphoneEchoGuardVolume = guardVolume;
    }
    return;
  }
  if (state.microphoneEchoUnmuteTimer) {
    return;
  }
  state.microphoneEchoUnmuteTimer = window.setTimeout(() => {
    state.microphoneEchoUnmuteTimer = 0;
    if (!state.microphoneEchoMuted || state.microphoneMuteReasons.size > 0) {
      return;
    }
    setMicrophoneCaptureVolumeBestEffort(state.microphoneCaptureVolume, label + " release");
    state.microphoneEchoMuted = false;
    state.microphoneEchoGuardVolume = state.microphoneCaptureVolume;
    log("Microphone capture restored after AI audio playback.", "ok");
  }, holdMs);
}

function readMicrophoneGuardVolume() {
  if (!state.microphoneGuardVolumes || state.microphoneGuardVolumes.size <= 0) {
    return state.microphoneCaptureVolume;
  }
  let volume = state.microphoneCaptureVolume;
  for (const value of state.microphoneGuardVolumes.values()) {
    volume = Math.min(volume, clampInteger(value, 0, 0, Math.max(0, state.microphoneCaptureVolume)));
  }
  return volume;
}

function setMicrophoneCaptureVolumeBestEffort(volume, reason) {
  try {
    state.engine.setCaptureVolume(StreamIndex.STREAM_INDEX_MAIN, volume);
  } catch (error) {
    log("Microphone capture volume change ignored (" + reason + "): " + error.message, "warn");
  }
}

async function pollCameraStatus() {
  try {
    const status = await fetchJson("/api/camera/status");
    state.cameraDesired = Boolean(status.desired);
    const settingsChanged = updateRuntimeSettings("cameraVideo", status.settings);
    updateCameraButtons();
    if (settingsChanged && state.cameraPublished) {
      restartFaceTrackingTimer();
      await applyCameraStreamSettings("bridge settings");
    }
    if (state.cameraDesired && !state.cameraPublished && Date.now() >= state.cameraNextRetryAt) {
      await startCameraBestEffort("bridge desired");
    } else if (!state.cameraDesired && state.cameraPublished) {
      await stopCameraBestEffort("bridge desired off");
    }
  } catch (error) {
    log("Camera status poll failed: " + error.message, "warn");
  }
}

async function requestCameraStart() {
  try {
    state.cameraDesired = true;
    state.cameraNextRetryAt = 0;
    await fetchJson("/api/camera/start", { method: "POST" });
    await startCameraBestEffort("manual click");
    await pollCameraStatus();
  } catch (error) {
    log("Camera stream request failed: " + error.message, "bad");
  }
}

async function requestCameraStop() {
  try {
    await fetchJson("/api/camera/stop", {
      method: "POST",
      body: JSON.stringify({ force: true, source: "web_manual" })
    });
    state.cameraDesired = false;
    state.cameraNextRetryAt = 0;
    await stopCameraBestEffort("manual click");
    await pollCameraStatus();
  } catch (error) {
    log("Camera stream stop failed: " + error.message, "bad");
  }
}

async function startCameraBestEffort(reason = "manual") {
  if (state.cameraChanging || state.cameraPublished) {
    return;
  }
  if (!state.engine || !state.joined) {
    log("Camera stream waits for RTC room join (" + reason + ").", "warn");
    markCameraNeedsUserGesture();
    return;
  }
  state.cameraChanging = true;
  el.cameraStart.classList.remove("attention");
  updateCameraButtons();
  try {
    setStatus("Starting camera stream...", "warn");
    const engine = state.engine;
    await applyCameraEncoderConfig(engine, "camera start");
    const settings = cameraVideoSettings();
    if (state.visionCompositeOnMain) {
      throw new Error("Screen vision is using main-video fallback; camera PiP is disabled. Use native screen sharing for dual stream.");
    }
    const externalTrackSupported = canUseExternalCameraTrack(engine, settings);
    if (settings.useCameraHub && !externalTrackSupported) {
      throw new Error("Camera Hub requires RTC external video track APIs; refusing to grab the real camera.");
    }
    if (externalTrackSupported) {
      const track = await ensureSharedCameraTrack();
      await engine.setVideoSourceType(StreamIndex.STREAM_INDEX_MAIN, VideoSourceType.VIDEO_SOURCE_TYPE_EXTERNAL);
      await engine.setExternalVideoTrack(StreamIndex.STREAM_INDEX_MAIN, track);
      state.cameraUsingExternalTrack = true;
      log("Camera capture shared as full RTC camera video.", "ok");
      if (settings.sendFaceTrackingPackets) {
        startBrowserFaceTracking().catch(error => log("Browser face tracking failed: " + error.message, "warn"));
      }
    } else {
      if (settings.useVirtualCamera || settings.requireVirtualCamera) {
        throw new Error("Virtual camera selection needs browser external track capture; SDK internal camera capture cannot be used.");
      }
      if (typeof engine.startVideoCapture !== "function") {
        throw new Error("Current RTC SDK does not expose camera capture APIs.");
      }
      await engine.startVideoCapture();
      state.cameraUsingExternalTrack = false;
      attachSdkLocalCameraTrack(engine);
      if (settings.sendFaceTrackingPackets) {
        startBrowserFaceTracking().catch(error => log("Browser face tracking failed: " + error.message, "warn"));
      }
    }
    await engine.publishStream(MediaType.VIDEO);
    state.cameraPublished = true;
    state.cameraNextRetryAt = 0;
    setStatus(state.screenPublished ? "Screen + camera streams running." : "Camera stream running.", "ok");
    log("Camera video stream published (" + reason + ").", "ok");
    await postCameraClientState(true, "camera stream published");
  } catch (error) {
    state.cameraPublished = false;
    stopBrowserFaceTracking();
    if (state.cameraUsingExternalTrack || state.cameraStream) {
      stopSharedCameraStream();
      state.cameraUsingExternalTrack = false;
    }
    setStatus(state.voiceStarted ? "Voice running; camera stream failed." : "Camera stream failed.", "warn");
    log("Camera stream start failed: " + error.message, "bad");
    state.cameraNextRetryAt = Date.now() + (isCameraDeviceBusyError(error) ? 5000 : 2500);
    if (isCameraDeviceBusyError(error)) {
      const settings = cameraVideoSettings();
      const route = settings.useVirtualCamera || settings.requireVirtualCamera ? "virtual camera" : (
        settings.useCameraHub ? "Camera Hub" : "browser camera"
      );
      log(
        "Camera source unavailable on " + route + ". Keeping the cloud camera request active and retrying; local scene tracking stays independent.",
        "warn"
      );
    } else if (state.cameraDesired) {
      markCameraNeedsUserGesture();
    }
    await postCameraClientState(false, "camera stream start failed: " + error.message);
  } finally {
    state.cameraChanging = false;
    updateCameraButtons();
  }
}

function isCameraDeviceBusyError(error) {
  const message = String(error && error.message ? error.message : error);
  return message.includes("NotReadableError") ||
    message.includes("Device in use") ||
    message.includes("Could not start video source");
}

function markCameraNeedsUserGesture() {
  el.cameraStart.classList.add("attention");
  updateCameraButtons();
  setStatus("Please click camera stream in this runtime window so the browser can request camera permission.", "warn");
  log("Camera stream needs a user click inside this runtime window before permission can be requested.", "warn");
}

function canUseExternalCameraTrack(engine, settings = cameraVideoSettings()) {
  const hasRtcExternalTrackApi = Boolean(
    engine &&
    typeof engine.setVideoSourceType === "function" &&
    typeof engine.setExternalVideoTrack === "function" &&
    StreamIndex &&
    VideoSourceType
  );
  if (!hasRtcExternalTrackApi) {
    return false;
  }
  if (settings && settings.useCameraHub) {
    return true;
  }
  return Boolean(
    navigator.mediaDevices &&
    typeof navigator.mediaDevices.getUserMedia === "function"
  );
}

function updateRuntimeSettings(key, settings) {
  if (!settings || typeof settings !== "object") {
    return false;
  }
  if (!state.config) {
    state.config = {};
  }
  const previous = JSON.stringify(state.config[key] || {});
  state.config[key] = {
    ...(state.config[key] || {}),
    ...settings
  };
  return JSON.stringify(state.config[key] || {}) !== previous;
}

function cameraVideoSettings() {
  const config = (state.config && state.config.cameraVideo) || {};
  return {
    width: clampInteger(config.width, 1280, 160, 1920),
    height: clampInteger(config.height, 720, 120, 1080),
    fps: clampInteger(config.fps ?? config.frameRate, 15, 5, 60),
    maxKbps: clampInteger(config.maxKbps ?? config.bitrateKbps, 3000, 100, 6000),
    faceTrackingPacketFps: clampInteger(config.faceTrackingPacketFps ?? config.packetFps, 15, 2, 30),
    sendFaceTrackingPackets: booleanSetting(config.sendFaceTrackingPackets, false),
    useCameraHub: booleanSetting(config.useCameraHub ?? config.cameraHub, false),
    cameraHubUrl: stringSetting(
      config.cameraHubUrl ?? config.cameraHubStreamUrl,
      "http://127.0.0.1:17863/stream.mjpg"
    ),
    useVirtualCamera: booleanSetting(config.useVirtualCamera ?? config.virtualCamera, true),
    requireVirtualCamera: booleanSetting(config.requireVirtualCamera, true),
    deviceKeyword: stringSetting(
      config.deviceKeyword ?? config.virtualCameraKeyword ?? config.cameraDeviceKeyword,
      "virtual,obs"
    )
  };
}

function screenVisionSettings() {
  const config = (state.config && state.config.screenVision) || {};
  return {
    width: clampInteger(config.width, 1280, 640, 3840),
    height: clampInteger(config.height, 720, 360, 2160),
    fps: clampInteger(config.fps ?? config.frameRate, 3, 1, 30),
    maxKbps: clampInteger(config.maxKbps ?? config.bitrateKbps, 3000, 500, 12000),
    cameraOverlayEnabled: booleanSetting(config.cameraOverlayEnabled, false),
    cameraOverlayWidth: clampInteger(config.cameraOverlayWidth, 640, 160, 1280),
    cameraOverlayHeight: clampInteger(config.cameraOverlayHeight, 360, 90, 720),
    cameraOverlayPadding: clampInteger(config.cameraOverlayPadding, 24, 0, 200),
    cameraOverlayPosition: stringSetting(config.cameraOverlayPosition, "bottomLeft"),
    cameraOverlaySourceUrl: stringSetting(
      config.cameraOverlaySourceUrl,
      "http://127.0.0.1:17863/stream.mjpg"
    )
  };
}

function clampInteger(value, fallback, min, max) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return fallback;
  }
  return Math.max(min, Math.min(max, Math.round(number)));
}

function booleanSetting(value, fallback) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "number") {
    return value !== 0;
  }
  if (typeof value === "string") {
    const text = value.trim().toLowerCase();
    if (["1", "true", "yes", "on", "enabled"].includes(text)) {
      return true;
    }
    if (["0", "false", "no", "off", "disabled"].includes(text)) {
      return false;
    }
  }
  return fallback;
}

function stringSetting(value, fallback) {
  const text = String(value ?? "").trim();
  return text || fallback;
}

async function applyCameraStreamSettings(reason = "settings") {
  await applyCameraEncoderConfig(state.engine, reason);
  await applyCameraTrackConstraints(reason);
}

async function applyCameraEncoderConfig(engine, reason = "settings") {
  if (!engine || typeof engine.setVideoEncoderConfig !== "function") {
    return;
  }
  const settings = cameraVideoSettings();
  try {
    await engine.setVideoEncoderConfig({
      width: settings.width,
      height: settings.height,
      frameRate: settings.fps,
      maxKbps: settings.maxKbps
    });
    log(
      "Camera encoder set to " + settings.width + "x" + settings.height + " @ " + settings.fps + "fps (" + reason + ").",
      "ok"
    );
  } catch (configError) {
    log("Camera encoder config ignored: " + configError.message, "warn");
  }
}

async function applyCameraTrackConstraints(reason = "settings") {
  if (!state.cameraTrack || typeof state.cameraTrack.applyConstraints !== "function") {
    return;
  }
  if (state.cameraHubCanvas) {
    return;
  }
  const settings = cameraVideoSettings();
  try {
    await state.cameraTrack.applyConstraints({
      width: { ideal: settings.width },
      height: { ideal: settings.height },
      frameRate: { ideal: settings.fps, max: Math.max(settings.fps, 30) }
    });
    log("Camera capture constraints applied (" + reason + ").", "ok");
  } catch (error) {
    log("Camera capture constraints ignored: " + error.message, "warn");
  }
}

function faceTrackingIntervalMs() {
  return Math.max(33, Math.round(1000 / cameraVideoSettings().faceTrackingPacketFps));
}

function restartFaceTrackingTimer() {
  if (!state.faceTrackingTimer) {
    return;
  }
  window.clearInterval(state.faceTrackingTimer);
  state.faceTrackingTimer = window.setInterval(() => processFaceTrackingFrame(), faceTrackingIntervalMs());
}

async function ensureSharedCameraTrack() {
  if (state.cameraTrack && state.cameraTrack.readyState === "live") {
    return state.cameraTrack;
  }
  const settings = cameraVideoSettings();
  if (settings.useCameraHub) {
    return await ensureCameraHubTrack(settings);
  }
  const deviceId = await resolveCameraDeviceId(settings);
  const constraints = {
    audio: false,
    video: {
      width: { ideal: settings.width },
      height: { ideal: settings.height },
      frameRate: { ideal: settings.fps, max: Math.max(settings.fps, 30) }
    }
  };
  if (deviceId) {
    constraints.video.deviceId = { exact: deviceId };
  }
  try {
    state.cameraStream = await navigator.mediaDevices.getUserMedia(constraints);
  } catch (error) {
    const label = state.cameraRequestedDeviceLabel || (settings.useVirtualCamera ? "virtual camera" : "default camera");
    const detail = [error && error.name, error && error.message].filter(Boolean).join(": ");
    throw new Error("getUserMedia failed for " + label + ": " + (detail || String(error)));
  }
  state.cameraTrack = state.cameraStream.getVideoTracks()[0] || null;
  if (!state.cameraTrack) {
    throw new Error("Browser camera returned no video track.");
  }
  const label = state.cameraTrack.label || "";
  if (settings.useVirtualCamera) {
    log("Virtual camera selected: " + (label || "selected device"), "ok");
  }
  return state.cameraTrack;
}

async function ensureCameraHubTrack(settings) {
  stopCameraHubSource();
  const sessionId = ++state.cameraHubSessionId;
  const canvas = document.createElement("canvas");
  canvas.width = settings.width;
  canvas.height = settings.height;
  const ctx = canvas.getContext("2d", { alpha: false });
  if (!ctx) {
    throw new Error("Browser canvas 2D context is unavailable for Camera Hub.");
  }
  ctx.fillStyle = "#050507";
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  const stream = createCanvasCaptureStream(canvas, settings.fps);
  const track = stream.getVideoTracks()[0] || null;
  if (!track) {
    throw new Error("Camera Hub canvas produced no video track.");
  }

  const snapshotUrl = cameraHubSnapshotUrl(settings.cameraHubUrl);
  state.cameraHubCanvas = canvas;
  state.cameraHubContext = ctx;
  state.cameraHubStream = stream;
  state.cameraHubTrack = track;
  state.cameraHubSnapshotUrl = snapshotUrl;
  state.cameraHubSnapshotLoading = false;
  state.cameraHubLastSnapshotErrorAt = 0;
  state.cameraStream = stream;
  state.cameraTrack = track;

  state.cameraHubImage = await loadCameraHubSnapshotImage(snapshotUrl, "Camera Hub snapshot", 3500);
  if (sessionId !== state.cameraHubSessionId) {
    throw new Error("Camera Hub start was superseded.");
  }
  drawCameraHubFrame(true);
  startCameraHubLoop(settings);

  const video = ensureFaceTrackingVideoElement();
  video.srcObject = stream;
  await video.play().catch(error => log("Camera Hub preview video play ignored: " + error.message, "warn"));
  log("Camera Hub attached: " + snapshotUrl, "ok");
  return track;
}

function cameraHubSnapshotUrl(url) {
  const text = String(url || "http://127.0.0.1:17863/snapshot.jpg").trim();
  return text.replace(/\/stream\.mjpg(?=($|\?))/i, "/snapshot.jpg");
}

function cameraHubUrlWithCache(url) {
  const separator = String(url).includes("?") ? "&" : "?";
  return String(url) + separator + "t=" + Date.now().toString();
}

async function loadCameraHubSnapshotImage(url, label, timeoutMs) {
  const deadline = performance.now() + Math.max(500, timeoutMs);
  let lastError = null;
  while (performance.now() < deadline) {
    try {
      const image = await fetchCameraHubSnapshotImage(url, Math.min(1500, Math.max(500, deadline - performance.now())));
      return image;
    } catch (error) {
      lastError = error;
      await sleep(120);
    }
  }
  throw new Error(label + " could not be loaded." + (lastError ? " " + lastError.message : ""));
}

async function fetchCameraHubSnapshotImage(url, timeoutMs, rememberObjectUrl = replaceCameraHubObjectUrl) {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), Math.max(300, timeoutMs));
  try {
    const response = await fetch(cameraHubUrlWithCache(url), {
      cache: "no-store",
      mode: "cors",
      signal: controller.signal
    });
    if (!response.ok) {
      throw new Error("HTTP " + response.status);
    }
    const blob = await response.blob();
    if (!blob || blob.size <= 0) {
      throw new Error("empty snapshot");
    }
    const objectUrl = URL.createObjectURL(blob);
    try {
      const image = createCameraHubImage();
      image.src = objectUrl;
      await waitForImageReady(image, "Camera Hub snapshot", timeoutMs);
      if (typeof rememberObjectUrl === "function") {
        rememberObjectUrl(objectUrl);
      }
      return image;
    } catch (error) {
      URL.revokeObjectURL(objectUrl);
      throw error;
    }
  } finally {
    window.clearTimeout(timer);
  }
}

function createCameraHubImage() {
  const image = new Image();
  image.decoding = "async";
  return image;
}

function replaceCameraHubObjectUrl(nextUrl) {
  if (state.cameraHubObjectUrl && state.cameraHubObjectUrl !== nextUrl) {
    URL.revokeObjectURL(state.cameraHubObjectUrl);
  }
  state.cameraHubObjectUrl = nextUrl || "";
}

function replaceScreenCameraOverlayObjectUrl(nextUrl) {
  if (state.screenCameraOverlayObjectUrl && state.screenCameraOverlayObjectUrl !== nextUrl) {
    URL.revokeObjectURL(state.screenCameraOverlayObjectUrl);
  }
  state.screenCameraOverlayObjectUrl = nextUrl || "";
}

async function waitForImageReady(image, label, timeoutMs) {
  if (image.naturalWidth && image.naturalHeight) {
    return true;
  }
  const startedAt = performance.now();
  return await new Promise((resolve, reject) => {
    let done = false;
    let timer = 0;
    let pollTimer = 0;
    const cleanup = (result, error) => {
      if (done) {
        return;
      }
      done = true;
      window.clearTimeout(timer);
      window.clearInterval(pollTimer);
      image.removeEventListener("load", check);
      image.removeEventListener("error", fail);
      if (error) {
        reject(error);
      } else {
        resolve(result);
      }
    };
    const check = () => {
      if (image.naturalWidth && image.naturalHeight) {
        cleanup(true, null);
      }
    };
    const fail = () => cleanup(false, new Error(label + " could not be loaded."));
    image.addEventListener("load", check);
    image.addEventListener("error", fail);
    pollTimer = window.setInterval(check, 50);
    timer = window.setTimeout(() => {
      cleanup(false, new Error(label + " stream timed out after " + Math.round(performance.now() - startedAt) + "ms."));
    }, Math.max(500, timeoutMs));
    check();
  });
}

function startCameraHubLoop(settings) {
  stopCameraHubLoop();
  const intervalMs = Math.max(66, 1000 / Math.max(1, Math.min(settings.fps, 15)));
  state.cameraHubLastDrawAt = 0;
  const draw = now => {
    if (!state.cameraHubCanvas || !state.cameraHubContext || !state.cameraHubImage) {
      state.cameraHubAnimationFrame = 0;
      return;
    }
    if (!state.cameraHubLastDrawAt || now - state.cameraHubLastDrawAt >= intervalMs) {
      state.cameraHubLastDrawAt = now;
      drawCameraHubFrame(false);
      requestCameraHubSnapshot();
    }
    state.cameraHubAnimationFrame = window.requestAnimationFrame(draw);
  };
  state.cameraHubAnimationFrame = window.requestAnimationFrame(draw);
}

function stopCameraHubLoop() {
  if (state.cameraHubAnimationFrame) {
    window.cancelAnimationFrame(state.cameraHubAnimationFrame);
    state.cameraHubAnimationFrame = 0;
  }
  state.cameraHubLastDrawAt = 0;
}

function drawCameraHubFrame(force) {
  const canvas = state.cameraHubCanvas;
  const ctx = state.cameraHubContext;
  const image = state.cameraHubImage;
  if (!canvas || !ctx || !image || !image.naturalWidth || !image.naturalHeight) {
    return;
  }
  if (!force && image.complete === false && !image.naturalWidth) {
    return;
  }
  ctx.fillStyle = "#050507";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  drawImageCover(ctx, image, image.naturalWidth, image.naturalHeight, 0, 0, canvas.width, canvas.height);
  if (state.cameraHubTrack && typeof state.cameraHubTrack.requestFrame === "function") {
    state.cameraHubTrack.requestFrame();
  }
}

function requestCameraHubSnapshot() {
  if (state.cameraHubSnapshotLoading || !state.cameraHubSnapshotUrl) {
    return;
  }
  const sessionId = state.cameraHubSessionId;
  let done = false;
  let timer = 0;
  const finish = (nextImage, error) => {
    if (done) {
      return;
    }
    done = true;
    window.clearTimeout(timer);
    state.cameraHubSnapshotLoading = false;
    if (sessionId !== state.cameraHubSessionId || !state.cameraHubCanvas) {
      return;
    }
    if (error) {
      const now = performance.now();
      if (!state.cameraHubLastSnapshotErrorAt || now - state.cameraHubLastSnapshotErrorAt > 5000) {
        state.cameraHubLastSnapshotErrorAt = now;
        log(error.message, "warn");
      }
      return;
    }
    state.cameraHubImage = nextImage;
    drawCameraHubFrame(false);
  };
  state.cameraHubSnapshotLoading = true;
  timer = window.setTimeout(() => finish(null, new Error("Camera Hub snapshot timed out.")), 2200);
  fetchCameraHubSnapshotImage(state.cameraHubSnapshotUrl, 2000)
    .then(image => finish(image, null))
    .catch(error => finish(null, new Error("Camera Hub snapshot could not be loaded. " + error.message)));
}

function drawImageCover(ctx, image, sourceWidth, sourceHeight, x, y, width, height) {
  const sourceRatio = sourceWidth / Math.max(1, sourceHeight);
  const targetRatio = width / Math.max(1, height);
  let sx = 0;
  let sy = 0;
  let sw = sourceWidth;
  let sh = sourceHeight;
  if (sourceRatio > targetRatio) {
    sw = sourceHeight * targetRatio;
    sx = (sourceWidth - sw) * 0.5;
  } else {
    sh = sourceWidth / targetRatio;
    sy = (sourceHeight - sh) * 0.5;
  }
  ctx.drawImage(image, sx, sy, sw, sh, x, y, width, height);
}

async function resolveCameraDeviceId(settings) {
  if (!settings.useVirtualCamera) {
    return "";
  }
  if (!navigator.mediaDevices || typeof navigator.mediaDevices.enumerateDevices !== "function") {
    if (settings.requireVirtualCamera) {
      throw new Error("Browser cannot enumerate cameras; virtual camera selection is unavailable.");
    }
    return "";
  }
  const keywords = splitDeviceKeywords(settings.deviceKeyword);
  const devices = await navigator.mediaDevices.enumerateDevices();
  const cameras = devices.filter(device => device.kind === "videoinput");
  state.cameraRequestedDeviceLabel = "";
  log("Camera devices: " + formatCameraDeviceList(cameras), "ok");
  const match = cameras.find(device => {
    const label = String(device.label || "").toLowerCase();
    return label && keywords.some(keyword => label.includes(keyword));
  });
  if (match) {
    state.cameraRequestedDeviceLabel = match.label || "matched virtual camera";
    log("Virtual camera matched: " + state.cameraRequestedDeviceLabel, "ok");
    return match.deviceId;
  }
  const labels = cameras.map(device => device.label).filter(Boolean).join(", ");
  if (settings.requireVirtualCamera) {
    throw new Error(
      "Virtual camera not found. Start OBS Virtual Camera/Unity virtual camera first. Available cameras: " +
      (labels || "camera labels are hidden until browser permission is granted")
    );
  }
  log("Virtual camera not found; falling back to default camera.", "warn");
  return "";
}

function formatCameraDeviceList(cameras) {
  if (!cameras.length) {
    return "none";
  }
  return cameras
    .map((device, index) => {
      const label = String(device.label || "").trim();
      return label ? (index + 1) + ":" + label : (index + 1) + ":<permission needed>";
    })
    .join(", ");
}

function splitDeviceKeywords(value) {
  const keywords = String(value || "virtual,obs")
    .split(/[,\n|;]+/)
    .map(item => item.trim().toLowerCase())
    .filter(Boolean);
  return keywords.length ? keywords : ["virtual", "obs"];
}

function attachSdkLocalCameraTrack(engine) {
  if (!engine || typeof engine.getLocalStreamTrack !== "function") {
    return;
  }
  const track = engine.getLocalStreamTrack(StreamIndex.STREAM_INDEX_MAIN, "video");
  if (!track) {
    return;
  }
  state.cameraTrack = track;
  state.cameraStream = new MediaStream([track]);
}

function stopSharedCameraStream() {
  stopCameraHubSource();
  if (state.cameraStream) {
    for (const track of state.cameraStream.getTracks()) {
      try {
        track.stop();
      } catch {
        // Ignore browser cleanup races.
      }
    }
  } else if (state.cameraTrack) {
    try {
      state.cameraTrack.stop();
    } catch {
      // Ignore browser cleanup races.
    }
  }
  state.cameraStream = null;
  state.cameraTrack = null;
  if (state.cameraVideo) {
    state.cameraVideo.srcObject = null;
  }
}

function stopCameraHubSource() {
  const hubStream = state.cameraHubStream;
  const hubTrack = state.cameraHubTrack;
  state.cameraHubSessionId += 1;
  stopCameraHubLoop();
  if (state.cameraHubImage) {
    state.cameraHubImage.src = "";
  }
  replaceCameraHubObjectUrl("");
  if (state.cameraHubStream) {
    for (const track of state.cameraHubStream.getTracks()) {
      try {
        track.stop();
      } catch {
        // Ignore browser cleanup races.
      }
    }
  } else if (state.cameraHubTrack) {
    try {
      state.cameraHubTrack.stop();
    } catch {
      // Ignore browser cleanup races.
    }
  }
  if (state.cameraStream === hubStream) {
    state.cameraStream = null;
  }
  if (state.cameraTrack === hubTrack) {
    state.cameraTrack = null;
  }
  state.cameraHubImage = null;
  state.cameraHubCanvas = null;
  state.cameraHubContext = null;
  state.cameraHubStream = null;
  state.cameraHubTrack = null;
  state.cameraHubSnapshotUrl = "";
  state.cameraHubSnapshotLoading = false;
  state.cameraHubLastSnapshotErrorAt = 0;
}

async function startBrowserFaceTracking() {
  if (state.faceTrackingActive) {
    return;
  }
  if (!state.cameraStream && state.cameraTrack) {
    state.cameraStream = new MediaStream([state.cameraTrack]);
  }
  if (!state.cameraStream) {
    log("Browser face tracking has no shared camera stream.", "warn");
    return;
  }
  const video = ensureFaceTrackingVideoElement();
  video.srcObject = state.cameraStream;
  await video.play();
  await ensureFaceLandmarker();
  state.faceTrackingActive = true;
  state.faceTrackingLastVideoTime = -1;
  if (!state.faceTrackingTimer) {
    state.faceTrackingTimer = window.setInterval(() => processFaceTrackingFrame(), faceTrackingIntervalMs());
  }
  log("Browser FaceLandmarker running with " + state.faceLandmarkerDelegate + " delegate.", "ok");
  window.setTimeout(() => processFaceTrackingFrame(), 0);
}

function ensureFaceTrackingVideoElement() {
  if (!state.cameraVideo) {
    const video = document.createElement("video");
    video.muted = true;
    video.autoplay = true;
    video.playsInline = true;
    video.style.display = "none";
    document.body.appendChild(video);
    state.cameraVideo = video;
  }
  return state.cameraVideo;
}

async function ensureFaceLandmarker() {
  if (state.faceLandmarker) {
    return state.faceLandmarker;
  }
  const fileset = await FilesetResolver.forVisionTasks("/vendor/mediapipe/wasm");
  try {
    state.faceLandmarker = await FaceLandmarker.createFromOptions(fileset, {
      baseOptions: {
        modelAssetPath: "/vendor/mediapipe/face_landmarker.task",
        delegate: "GPU"
      },
      runningMode: "VIDEO",
      numFaces: 1,
      minFaceDetectionConfidence: 0.55,
      minFacePresenceConfidence: 0.55,
      minTrackingConfidence: 0.55,
      outputFaceBlendshapes: false,
      outputFacialTransformationMatrixes: false
    });
    state.faceLandmarkerDelegate = "GPU";
    return state.faceLandmarker;
  } catch (gpuError) {
    log("FaceLandmarker GPU delegate failed, falling back to CPU: " + gpuError.message, "warn");
    state.faceLandmarker = await FaceLandmarker.createFromOptions(fileset, {
      baseOptions: {
        modelAssetPath: "/vendor/mediapipe/face_landmarker.task",
        delegate: "CPU"
      },
      runningMode: "VIDEO",
      numFaces: 1,
      minFaceDetectionConfidence: 0.55,
      minFacePresenceConfidence: 0.55,
      minTrackingConfidence: 0.55,
      outputFaceBlendshapes: false,
      outputFacialTransformationMatrixes: false
    });
    state.faceLandmarkerDelegate = "CPU";
    return state.faceLandmarker;
  }
}

function stopBrowserFaceTracking() {
  state.faceTrackingActive = false;
  if (state.faceTrackingTimer) {
    window.clearInterval(state.faceTrackingTimer);
    state.faceTrackingTimer = 0;
  }
  state.faceTrackingLastVideoTime = -1;
  state.faceTrackingPacketLogCount = 0;
  postFaceTrackingPacket(emptyFacePacket()).catch(() => null);
}

function processFaceTrackingFrame() {
  if (!state.faceTrackingActive || !state.faceLandmarker || !state.cameraVideo) {
    return;
  }
  const video = state.cameraVideo;
  if (video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !video.videoWidth || !video.videoHeight) {
    return;
  }
  state.faceTrackingLastVideoTime = video.currentTime;
  let packet = null;
  try {
    const result = state.faceLandmarker.detectForVideo(video, performance.now());
    const landmarks = result.faceLandmarks && result.faceLandmarks[0];
    packet = landmarks ? measureFacePacket(landmarks, video.videoWidth, video.videoHeight) : emptyFacePacket();
  } catch (error) {
    log("Browser face tracking frame failed: " + error.message, "warn");
    return;
  }
  const now = performance.now();
  if (state.faceTrackingInFlight || now - state.faceTrackingLastPacketAt < faceTrackingIntervalMs()) {
    return;
  }
  state.faceTrackingLastPacketAt = now;
  postFaceTrackingPacket(packet).catch(error => log("Face tracking packet failed: " + error.message, "warn"));
}

async function postFaceTrackingPacket(packet) {
  state.faceTrackingInFlight = true;
  packet.source = packet.source || "bridge_camera";
  try {
    const response = await fetchJson("/api/face_tracking/packet", {
      method: "POST",
      body: JSON.stringify(packet)
    });
    if (response && response.ok) {
      state.faceTrackingPacketLogCount += 1;
      if (state.faceTrackingPacketLogCount === 1 || state.faceTrackingPacketLogCount % 120 === 0) {
        log(
          "Face tracking packet sent: face=" + Boolean(packet.face_found) +
          " yaw=" + Math.round(packet.yaw || 0) +
          " pitch=" + Math.round(packet.pitch || 0),
          "ok"
        );
      }
    }
  } finally {
    state.faceTrackingInFlight = false;
  }
}

function emptyFacePacket() {
  return {
    source: "bridge_camera",
    face_found: false,
    face_center_x: 0,
    face_center_y: 0,
    face_width_px: 0,
    yaw: 0,
    pitch: 0,
    roll: 0,
    z_cm: 0,
    z_offset: 0,
    timestamp: Date.now() / 1000
  };
}

function measureFacePacket(landmarks, width, height) {
  const xs = landmarks.map(point => point.x * width);
  const ys = landmarks.map(point => point.y * height);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  const faceWidthPx = Math.max(maxX - minX, 1);
  const centerXPx = (minX + maxX) * 0.5;
  const centerYPx = (minY + maxY) * 0.5;
  const normalizedX = clamp((centerXPx / Math.max(width, 1) - 0.5) * 2, -1, 1);
  const normalizedY = clamp(-(centerYPx / Math.max(height, 1) - 0.5) * 2, -1, 1);
  const defaultDistanceCm = 60;
  const baselineFaceWidthPx = 170;
  const zCm = defaultDistanceCm * baselineFaceWidthPx / faceWidthPx;
  const zOffset = clamp((defaultDistanceCm - zCm) / defaultDistanceCm, -1, 1);
  const pose = estimatePoseDegrees(landmarks, width, height, faceWidthPx);
  return {
    source: "bridge_camera",
    face_found: true,
    face_center_x: normalizedX,
    face_center_y: normalizedY,
    face_width_px: faceWidthPx,
    yaw: pose.yaw,
    pitch: pose.pitch,
    roll: pose.roll,
    z_cm: zCm,
    z_offset: zOffset,
    timestamp: Date.now() / 1000
  };
}

function estimatePoseDegrees(landmarks, width, height, faceWidthPx) {
  const leftEye = landmarkXY(landmarks, 33, width, height);
  const rightEye = landmarkXY(landmarks, 263, width, height);
  const nose = landmarkXY(landmarks, 1, width, height);
  const mouthLeft = landmarkXY(landmarks, 61, width, height);
  const mouthRight = landmarkXY(landmarks, 291, width, height);
  const eyeMidX = (leftEye.x + rightEye.x) * 0.5;
  const eyeMidY = (leftEye.y + rightEye.y) * 0.5;
  const mouthMidY = (mouthLeft.y + mouthRight.y) * 0.5;
  const roll = radiansToDegrees(Math.atan2(rightEye.y - leftEye.y, rightEye.x - leftEye.x));
  const yaw = clamp((nose.x - eyeMidX) / Math.max(faceWidthPx, 1) * 95, -45, 45);
  const eyeToMouth = Math.max(mouthMidY - eyeMidY, 1);
  const noseRatio = (nose.y - eyeMidY) / eyeToMouth;
  const pitch = clamp((0.52 - noseRatio) * 85, -35, 35);
  return { yaw, pitch, roll };
}

function landmarkXY(landmarks, index, width, height) {
  const point = landmarks[index] || { x: 0.5, y: 0.5 };
  return { x: point.x * width, y: point.y * height };
}

function radiansToDegrees(value) {
  return value * 180 / Math.PI;
}

function clamp(value, low, high) {
  return Math.max(low, Math.min(high, value));
}

async function stopCameraBestEffort(reason = "manual") {
  if (state.cameraChanging) {
    return;
  }
  const engine = state.engine;
  state.cameraChanging = true;
  updateCameraButtons();
  try {
    if (state.visionCompositeOnMain) {
      stopBrowserFaceTracking();
      stopSharedCameraStream();
      state.cameraPublished = false;
      state.cameraUsingExternalTrack = false;
      log("Camera stream state cleared while screen fallback is active (" + reason + ").", "warn");
      await postCameraClientState(false, "camera stream state cleared");
      return;
    }
    if (engine) {
      if (state.cameraPublished) {
        await tryCall(() => engine.unpublishStream(MediaType.VIDEO), "Unpublish camera video");
      }
      if (state.cameraUsingExternalTrack) {
        stopBrowserFaceTracking();
        stopSharedCameraStream();
      } else if (typeof engine.stopVideoCapture === "function") {
        stopBrowserFaceTracking();
        await tryCall(() => engine.stopVideoCapture(), "Stop camera capture");
      } else {
        stopBrowserFaceTracking();
      }
    }
    if (!state.cameraUsingExternalTrack) {
      state.cameraStream = null;
      state.cameraTrack = null;
      if (state.cameraVideo) {
        state.cameraVideo.srcObject = null;
      }
    }
    state.cameraPublished = false;
    state.cameraUsingExternalTrack = false;
    log("Camera stream stopped (" + reason + ").", "warn");
    await postCameraClientState(false, "camera stream stopped");
  } finally {
    state.cameraChanging = false;
    updateCameraButtons();
  }
}

async function postCameraClientState(cameraPublished, message) {
  try {
    await fetchJson("/api/camera/client_state", {
      method: "POST",
      body: JSON.stringify({
        cameraPublished,
        message
      })
    });
  } catch (error) {
    log("Camera client state report failed: " + error.message, "warn");
  }
}

async function startScreenVisionBestEffort(reason = "manual") {
  if (state.visionChanging || state.screenPublished) {
    return;
  }
  if (!state.engine || !state.joined) {
    log("Screen vision waits for RTC room join (" + reason + ").", "warn");
    markVisionNeedsUserGesture();
    return;
  }
  state.visionChanging = true;
  el.visionStart.classList.remove("attention");
  updateVisionButtons();
  try {
    setStatus("Starting screen vision...", "warn");
    const settings = screenVisionSettings();
    const screenEncoderConfig = {
      width: settings.width,
      height: settings.height,
      frameRate: settings.fps,
      maxKbps: settings.maxKbps,
      contentHint: "detail"
    };
    const captureConfig = {
      video: {
        width: { ideal: settings.width },
        height: { ideal: settings.height },
        frameRate: { ideal: settings.fps, max: Math.max(settings.fps, 4) }
      },
      audio: false
    };
    if (typeof state.engine.publishScreen !== "function" && !canUseExternalScreenTrack(state.engine)) {
      throw new Error("Current RTC SDK does not expose screen capture APIs.");
    }
    if (typeof state.engine.setScreenEncoderConfig === "function") {
      await state.engine.setScreenEncoderConfig(screenEncoderConfig);
      log("Screen encoder set to " + settings.width + "x" + settings.height + " @ " + settings.fps + "fps.", "ok");
    }

    const forceComposedScreen = settings.cameraOverlayEnabled;
    let nativeScreenStarted = false;
    if (!forceComposedScreen && typeof state.engine.startScreenCapture === "function" && typeof state.engine.publishScreen === "function") {
      try {
        await state.engine.startScreenCapture(captureConfig);
        nativeScreenStarted = true;
      } catch (captureError) {
        const captureMessage = String(captureError && captureError.message ? captureError.message : captureError);
        if (
          captureMessage.includes("Overconstrained") ||
          captureMessage.includes("constraint") ||
          captureMessage.includes("not satisfied")
        ) {
          log("Screen capture constraint failed, retrying with SDK default: " + captureMessage, "warn");
          await state.engine.startScreenCapture({ audio: false });
          nativeScreenStarted = true;
        } else if (canUseExternalScreenTrack(state.engine)) {
          log("Native screen capture failed, falling back to screen-only external track: " + captureMessage, "warn");
        } else {
          throw captureError;
        }
      }
    }

    if (nativeScreenStarted) {
      if (state.screenUsingExternalTrack && typeof state.engine.setVideoSourceType === "function") {
        await tryCall(
          () => state.engine.setVideoSourceType(StreamIndex.STREAM_INDEX_SCREEN, VideoSourceType.VIDEO_SOURCE_TYPE_INTERNAL),
          "Restore internal screen source"
        );
      }
      state.screenUsingExternalTrack = false;
      state.visionCompositeOnMain = false;
    } else {
      if (!canUseExternalScreenTrack(state.engine)) {
        throw new Error("Current RTC SDK does not expose usable screen capture APIs.");
      }
      if (!forceComposedScreen && state.cameraPublished) {
        throw new Error("Native screen sharing failed; refusing to replace the separate camera stream with a fallback composite.");
      }
      const compositeTrack = await ensureCompositeScreenTrack(captureConfig, settings);
      const streamIndex = forceComposedScreen ? StreamIndex.STREAM_INDEX_SCREEN : StreamIndex.STREAM_INDEX_MAIN;
      await state.engine.setVideoSourceType(streamIndex, VideoSourceType.VIDEO_SOURCE_TYPE_EXTERNAL);
      await state.engine.setExternalVideoTrack(streamIndex, compositeTrack);
      state.screenUsingExternalTrack = true;
      state.visionCompositeOnMain = !forceComposedScreen;
      log(forceComposedScreen
        ? "Screen stream uses a composed track with camera overlay for cloud vision."
        : "Vision fallback uses the main video stream with a screen-only composite track; camera PiP is disabled.", "warn");
    }
    if (state.visionCompositeOnMain) {
      await state.engine.publishStream(MediaType.VIDEO);
    } else {
      await state.engine.publishScreen(MediaType.VIDEO);
    }
    state.screenPublished = true;
    setStatus("Screen vision running.", "ok");
    log(state.visionCompositeOnMain
      ? "Vision composite stream published on main video."
      : "Screen stream published for the selected voice route.", "ok");
    await postVisionClientState(true, state.visionCompositeOnMain ? "vision composite main stream published" : "screen stream published");
  } catch (error) {
    state.screenPublished = false;
    state.screenUsingExternalTrack = false;
    state.visionCompositeOnMain = false;
    stopCompositeScreenCapture();
    setStatus(state.voiceStarted ? "Voice running; screen vision failed." : "Screen vision failed.", "warn");
    log("Screen vision start failed: " + error.message, "bad");
    if (state.visionDesired) {
      markVisionNeedsUserGesture();
    }
    await postVisionClientState(false, "screen vision start failed: " + error.message);
  } finally {
    state.visionChanging = false;
    updateVisionButtons();
  }
}

async function startScreenVisionBeforeVoiceBestEffort() {
  const timeoutMs = 7000;
  const started = await Promise.race([
    startScreenVisionBestEffort("before StartVoiceChat").then(() => state.screenPublished),
    sleep(timeoutMs).then(() => false)
  ]);
  if (!started) {
    log("Screen vision was not published before StartVoiceChat; continuing voice startup.", "warn");
  }
}

function canUseExternalScreenTrack(engine) {
  return Boolean(
    engine &&
    typeof engine.setVideoSourceType === "function" &&
    typeof engine.setExternalVideoTrack === "function" &&
    StreamIndex &&
    VideoSourceType &&
    navigator.mediaDevices &&
    typeof navigator.mediaDevices.getDisplayMedia === "function" &&
    typeof HTMLCanvasElement !== "undefined" &&
    HTMLCanvasElement.prototype &&
    typeof HTMLCanvasElement.prototype.captureStream === "function"
  );
}

async function ensureCompositeScreenTrack(captureConfig, settings) {
  stopCompositeScreenCapture();
  state.screenStream = await navigator.mediaDevices.getDisplayMedia(captureConfig);
  state.screenTrack = state.screenStream.getVideoTracks()[0] || null;
  if (!state.screenTrack) {
    throw new Error("Browser screen capture returned no video track.");
  }
  state.screenTrack.contentHint = "detail";
  state.screenTrack.addEventListener("ended", () => requestVisionStop().catch(() => null), { once: true });

  const screenVideo = ensureCompositeScreenVideo();
  screenVideo.srcObject = state.screenStream;
  await screenVideo.play();
  const screenReady = await waitForVideoReady(screenVideo, "screen capture", 3500);
  if (!screenReady) {
    log("Screen capture video did not produce a readable frame yet; publishing composed track with forced canvas frames.", "warn");
  }

  const canvas = ensureCompositeScreenCanvas(settings);
  if (settings.cameraOverlayEnabled) {
    await ensureScreenCameraOverlaySource(settings);
  } else {
    clearScreenCameraOverlaySource();
  }
  drawCompositeScreenFrame();
  const composedStream = createCanvasCaptureStream(canvas, settings.fps);
  state.screenCompositeTrack = composedStream.getVideoTracks()[0] || null;
  if (!state.screenCompositeTrack) {
    throw new Error("Browser canvas capture returned no video track.");
  }
  state.screenCompositeTrack.contentHint = "detail";
  drawCompositeScreenFrame();
  startCompositeScreenLoop(settings);
  return state.screenCompositeTrack;
}

function ensureCompositeScreenVideo() {
  if (!state.screenVideo) {
    const video = document.createElement("video");
    video.muted = true;
    video.autoplay = true;
    video.playsInline = true;
    video.style.display = "none";
    document.body.appendChild(video);
    state.screenVideo = video;
  }
  return state.screenVideo;
}

function ensureCompositeScreenCanvas(settings) {
  if (!state.screenCompositeCanvas) {
    const canvas = document.createElement("canvas");
    canvas.style.display = "none";
    document.body.appendChild(canvas);
    state.screenCompositeCanvas = canvas;
  }
  state.screenCompositeCanvas.width = settings.width;
  state.screenCompositeCanvas.height = settings.height;
  state.screenCompositeContext = state.screenCompositeCanvas.getContext("2d", { alpha: false });
  if (!state.screenCompositeContext) {
    throw new Error("Browser canvas 2D context is unavailable.");
  }
  return state.screenCompositeCanvas;
}

function createCanvasCaptureStream(canvas, fps) {
  const manualStream = canvas.captureStream(0);
  const manualTrack = manualStream.getVideoTracks()[0] || null;
  if (manualTrack && typeof manualTrack.requestFrame === "function") {
    return manualStream;
  }
  if (manualTrack) {
    manualTrack.stop();
  }
  return canvas.captureStream(fps);
}

function isVideoReady(video) {
  return Boolean(
    video &&
    video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA &&
    video.videoWidth &&
    video.videoHeight
  );
}

async function waitForVideoReady(video, label, timeoutMs) {
  if (isVideoReady(video)) {
    return true;
  }
  const startedAt = performance.now();
  return await new Promise(resolve => {
    let done = false;
    let timer = 0;
    let pollTimer = 0;
    const cleanup = result => {
      if (done) {
        return;
      }
      done = true;
      window.clearTimeout(timer);
      window.clearInterval(pollTimer);
      video.removeEventListener("loadedmetadata", check);
      video.removeEventListener("playing", check);
      video.removeEventListener("timeupdate", check);
      resolve(result);
    };
    const check = () => {
      if (isVideoReady(video)) {
        cleanup(true);
      }
    };
    video.addEventListener("loadedmetadata", check);
    video.addEventListener("playing", check);
    video.addEventListener("timeupdate", check);
    pollTimer = window.setInterval(check, 80);
    timer = window.setTimeout(() => {
      log(label + " frame wait timed out after " + Math.round(performance.now() - startedAt) + "ms.", "warn");
      cleanup(isVideoReady(video));
    }, Math.max(500, timeoutMs));
    check();
  });
}

function startCompositeScreenLoop(settings) {
  stopCompositeScreenLoop();
  const intervalMs = Math.max(33, 1000 / Math.max(1, settings.fps));
  state.screenCompositeLastDrawAt = 0;
  const draw = now => {
    if (!state.screenCompositeCanvas || !state.screenCompositeContext || !state.screenVideo) {
      state.screenCompositeAnimationFrame = 0;
      return;
    }
    if (!state.screenCompositeLastDrawAt || now - state.screenCompositeLastDrawAt >= intervalMs) {
      state.screenCompositeLastDrawAt = now;
      drawCompositeScreenFrame();
    }
    state.screenCompositeAnimationFrame = window.requestAnimationFrame(draw);
  };
  state.screenCompositeAnimationFrame = window.requestAnimationFrame(draw);
}

async function ensureScreenCameraOverlaySource(settings) {
  const snapshotUrl = cameraHubSnapshotUrl(settings.cameraOverlaySourceUrl);
  state.screenCameraOverlaySnapshotUrl = snapshotUrl;
  state.screenCameraOverlaySnapshotLoading = false;
  state.screenCameraOverlayLastSnapshotErrorAt = 0;
  try {
    state.screenCameraOverlayImage = await fetchCameraHubSnapshotImage(
      snapshotUrl,
      2500,
      replaceScreenCameraOverlayObjectUrl
    );
    log("Camera overlay attached to screen stream: " + snapshotUrl, "ok");
  } catch (error) {
    state.screenCameraOverlayImage = null;
    log("Camera overlay snapshot could not be loaded yet: " + error.message, "warn");
  }
}

function requestScreenCameraOverlaySnapshot() {
  if (state.screenCameraOverlaySnapshotLoading || !state.screenCameraOverlaySnapshotUrl) {
    return;
  }
  let done = false;
  let timer = 0;
  const finish = (nextImage, error) => {
    if (done) {
      return;
    }
    done = true;
    window.clearTimeout(timer);
    state.screenCameraOverlaySnapshotLoading = false;
    if (!state.screenCameraOverlaySnapshotUrl) {
      return;
    }
    if (error) {
      const now = performance.now();
      if (!state.screenCameraOverlayLastSnapshotErrorAt || now - state.screenCameraOverlayLastSnapshotErrorAt > 5000) {
        state.screenCameraOverlayLastSnapshotErrorAt = now;
        log(error.message, "warn");
      }
      return;
    }
    state.screenCameraOverlayImage = nextImage;
  };
  state.screenCameraOverlaySnapshotLoading = true;
  timer = window.setTimeout(() => finish(null, new Error("Camera overlay snapshot timed out.")), 2200);
  fetchCameraHubSnapshotImage(
    state.screenCameraOverlaySnapshotUrl,
    2000,
    replaceScreenCameraOverlayObjectUrl
  )
    .then(image => finish(image, null))
    .catch(error => finish(null, new Error("Camera overlay snapshot could not be loaded. " + error.message)));
}

function clearScreenCameraOverlaySource() {
  if (state.screenCameraOverlayImage) {
    state.screenCameraOverlayImage.src = "";
  }
  replaceScreenCameraOverlayObjectUrl("");
  state.screenCameraOverlayImage = null;
  state.screenCameraOverlaySnapshotUrl = "";
  state.screenCameraOverlaySnapshotLoading = false;
  state.screenCameraOverlayLastSnapshotErrorAt = 0;
}

function drawCompositeScreenFrame() {
  const canvas = state.screenCompositeCanvas;
  const ctx = state.screenCompositeContext;
  const screenVideo = state.screenVideo;
  ctx.fillStyle = "#050507";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  drawVideoContain(ctx, screenVideo, 0, 0, canvas.width, canvas.height);

  drawScreenCameraOverlay(ctx, canvas, screenVisionSettings());
  drawCompositeFramePulse(ctx, canvas);
  requestCompositeScreenFrame();
}

function drawScreenCameraOverlay(ctx, canvas, settings) {
  if (!settings.cameraOverlayEnabled) {
    return;
  }
  const rect = screenCameraOverlayRect(canvas, settings);
  const image = state.screenCameraOverlayImage;
  ctx.save();
  ctx.fillStyle = "rgba(5, 8, 14, 0.82)";
  ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
  if (image && image.naturalWidth && image.naturalHeight) {
    drawImageCover(ctx, image, image.naturalWidth, image.naturalHeight, rect.x, rect.y, rect.width, rect.height);
  }
  ctx.lineWidth = Math.max(2, Math.round(canvas.width / 960));
  ctx.strokeStyle = "rgba(235, 246, 255, 0.92)";
  ctx.strokeRect(rect.x + 0.5, rect.y + 0.5, rect.width - 1, rect.height - 1);
  ctx.restore();
  requestScreenCameraOverlaySnapshot();
}

function screenCameraOverlayRect(canvas, settings) {
  const padding = clamp(settings.cameraOverlayPadding, 0, Math.min(canvas.width, canvas.height) * 0.2);
  const maxWidth = Math.max(160, canvas.width - padding * 2);
  const maxHeight = Math.max(90, canvas.height - padding * 2);
  let width = clamp(settings.cameraOverlayWidth, 160, Math.min(maxWidth, canvas.width * 0.7));
  let height = clamp(settings.cameraOverlayHeight, 90, Math.min(maxHeight, canvas.height * 0.7));
  const desiredRatio = 16 / 9;
  if (Math.abs(width / Math.max(1, height) - desiredRatio) > 0.02) {
    height = width / desiredRatio;
    if (height > maxHeight) {
      height = maxHeight;
      width = height * desiredRatio;
    }
  }
  const position = String(settings.cameraOverlayPosition || "bottomLeft").toLowerCase();
  const x = position.includes("right") ? canvas.width - width - padding : padding;
  const y = position.includes("top") ? padding : canvas.height - height - padding;
  return {
    x: Math.round(x),
    y: Math.round(y),
    width: Math.round(width),
    height: Math.round(height)
  };
}

function drawCompositeFramePulse(ctx, canvas) {
  const pulse = 6 + (state.screenCompositeFrameCounter++ % 2);
  ctx.fillStyle = "rgb(" + pulse + "," + pulse + "," + pulse + ")";
  ctx.fillRect(canvas.width - 1, canvas.height - 1, 1, 1);
}

function requestCompositeScreenFrame() {
  if (state.screenCompositeTrack && typeof state.screenCompositeTrack.requestFrame === "function") {
    state.screenCompositeTrack.requestFrame();
  }
}

function drawVideoContain(ctx, video, x, y, width, height) {
  if (!video || video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !video.videoWidth || !video.videoHeight) {
    return;
  }
  const scale = Math.min(width / video.videoWidth, height / video.videoHeight);
  const drawWidth = video.videoWidth * scale;
  const drawHeight = video.videoHeight * scale;
  ctx.drawImage(video, x + (width - drawWidth) * 0.5, y + (height - drawHeight) * 0.5, drawWidth, drawHeight);
}

function drawVideoCover(ctx, video, x, y, width, height) {
  const sourceRatio = video.videoWidth / Math.max(1, video.videoHeight);
  const targetRatio = width / Math.max(1, height);
  let sx = 0;
  let sy = 0;
  let sw = video.videoWidth;
  let sh = video.videoHeight;
  if (sourceRatio > targetRatio) {
    sw = video.videoHeight * targetRatio;
    sx = (video.videoWidth - sw) * 0.5;
  } else {
    sh = video.videoWidth / targetRatio;
    sy = (video.videoHeight - sh) * 0.5;
  }
  ctx.drawImage(video, sx, sy, sw, sh, x, y, width, height);
}

function stopCompositeScreenCapture() {
  stopCompositeScreenLoop();
  if (state.screenCompositeTrack) {
    try {
      state.screenCompositeTrack.stop();
    } catch {
      // Ignore browser cleanup races.
    }
  }
  if (state.screenStream) {
    for (const track of state.screenStream.getTracks()) {
      try {
        track.stop();
      } catch {
        // Ignore browser cleanup races.
      }
    }
  }
  if (state.screenVideo) {
    state.screenVideo.srcObject = null;
  }
  clearScreenCameraOverlaySource();
  state.screenStream = null;
  state.screenTrack = null;
  state.screenCompositeTrack = null;
  state.screenCompositeFrameCounter = 0;
}

function stopCompositeScreenLoop() {
  if (state.screenCompositeAnimationFrame) {
    window.cancelAnimationFrame(state.screenCompositeAnimationFrame);
    state.screenCompositeAnimationFrame = 0;
  }
  state.screenCompositeLastDrawAt = 0;
}

function markVisionNeedsUserGesture() {
  el.visionStart.classList.add("attention");
  updateVisionButtons();
  setStatus("请在这个窗口点击“开启屏幕识别”，然后选择要共享的屏幕或窗口。", "warn");
  log("Screen vision needs a user click inside this runtime window before the system picker can open.", "warn");
}

async function stopScreenVisionBestEffort(reason = "manual") {
  if (state.visionChanging) {
    return;
  }
  const engine = state.engine;
  state.visionChanging = true;
  updateVisionButtons();
  try {
    if (engine) {
      if (state.visionCompositeOnMain) {
        if (state.screenPublished) {
          await tryCall(() => engine.unpublishStream(MediaType.VIDEO), "Unpublish vision composite video");
        }
        if (state.cameraPublished) {
          stopBrowserFaceTracking();
          stopSharedCameraStream();
          state.cameraPublished = false;
          state.cameraUsingExternalTrack = false;
          await postCameraClientState(false, "camera stream cleared with vision composite");
        }
        stopCompositeScreenCapture();
        if (typeof engine.setVideoSourceType === "function") {
          await tryCall(
            () => engine.setVideoSourceType(StreamIndex.STREAM_INDEX_MAIN, VideoSourceType.VIDEO_SOURCE_TYPE_INTERNAL),
            "Restore internal main video source"
          );
        }
      } else if (state.screenPublished && typeof engine.unpublishScreen === "function") {
        await tryCall(() => engine.unpublishScreen(MediaType.VIDEO), "Unpublish screen");
        if (state.screenUsingExternalTrack) {
          stopCompositeScreenCapture();
          if (typeof engine.setVideoSourceType === "function") {
            await tryCall(
              () => engine.setVideoSourceType(StreamIndex.STREAM_INDEX_SCREEN, VideoSourceType.VIDEO_SOURCE_TYPE_INTERNAL),
              "Restore internal screen source"
            );
          }
        }
      } else if (typeof engine.stopScreenCapture === "function") {
        await tryCall(() => engine.stopScreenCapture(), "Stop screen capture");
      }
    } else if (state.screenUsingExternalTrack) {
      stopCompositeScreenCapture();
    }
    state.screenPublished = false;
    state.screenUsingExternalTrack = false;
    state.visionCompositeOnMain = false;
    log("Screen vision stopped (" + reason + ").", "warn");
    await postVisionClientState(false, "screen stream stopped");
  } finally {
    state.visionChanging = false;
    updateVisionButtons();
  }
}

async function postVisionClientState(screenPublished, message) {
  try {
    await fetchJson("/api/vision/client_state", {
      method: "POST",
      body: JSON.stringify({
        screenPublished,
        message
      })
    });
  } catch (error) {
    log("Vision client state report failed: " + error.message, "warn");
  }
}

async function startSubtitleBestEffort(engine, phase = "manual") {
  if (!el.clientSubtitle.checked || typeof engine.startSubtitle !== "function") {
    return;
  }
  if (state.subtitleStarted) {
    return;
  }
  try {
    await engine.startSubtitle({ mode: SUBTITLE_MODE.ASR_ONLY });
    state.subtitleStarted = true;
    log("Extra RTC subtitle API started (" + phase + ").", "ok");
  } catch (error) {
    const message = String(error && error.message ? error.message : error);
    if (message.includes("SUBTITLE_ALREADY_ON")) {
      state.subtitleStarted = true;
      log("Extra RTC subtitle API already on (" + phase + ").", "ok");
      return;
    }
    log("Extra RTC subtitle API failed (" + phase + "): " + message, "warn");
  }
}

async function stopSession() {
  if (state.stopping) {
    return;
  }
  const shouldStopCloudTask = state.voiceStarted;
  state.voiceStarted = false;
  state.eventForwardingActive = false;
  state.visionDesired = false;
  state.cameraDesired = false;
  state.lastStopAt = Date.now();
  state.stopping = true;
  setBusy(true);
  try {
    await fetchJson("/api/vision/stop", { method: "POST" }).catch(() => null);
    await fetchJson("/api/camera/stop", {
      method: "POST",
      body: JSON.stringify({ force: true, source: "voice_stop" })
    }).catch(() => null);
    if (shouldStopCloudTask) {
      await fetchJson("/api/stop_voice_chat", { method: "POST" });
      log("StopVoiceChat called.", "ok");
    }
  } catch (error) {
    log("StopVoiceChat failed: " + error.message, "warn");
  }

  const engine = state.engine;
  if (engine) {
    await stopSubscribedBotAudio("voice stop");
    if (state.screenPublished) {
      await stopScreenVisionBestEffort("voice stop");
    }
    if (state.cameraPublished) {
      await stopCameraBestEffort("voice stop");
    }
    if (state.subtitleStarted && typeof engine.stopSubtitle === "function") {
      await tryCall(() => engine.stopSubtitle(), "Stop subtitle");
    }
    if (state.audioPublished) {
      await tryCall(() => engine.unpublishStream(MediaType.AUDIO), "Unpublish audio");
    }
    await tryCall(() => engine.stopAudioCapture(), "Stop microphone");
    await tryCall(() => engine.leaveRoom(), "Leave RTC room");
    try {
      VERTC.destroyEngine(engine);
    } catch (error) {
      log("Destroy RTC engine failed: " + error.message, "warn");
    }
  }

  state.engine = null;
  state.joined = false;
  state.audioPublished = false;
  state.remoteBotAudioSubscriptions.clear();
  if (state.microphoneEchoUnmuteTimer) {
    window.clearTimeout(state.microphoneEchoUnmuteTimer);
    state.microphoneEchoUnmuteTimer = 0;
  }
  state.microphoneMuteReasons.clear();
  state.microphoneEchoMuted = false;
  state.screenPublished = false;
  state.cameraPublished = false;
  state.subtitleStarted = false;
  setStatus("Stopped.", "warn");
  el.stop.disabled = true;
  state.stopping = false;
  setBusy(false);
}

function stopSessionBeacon() {
  if (!state.voiceStarted && !state.joined && !state.screenPublished && !state.cameraPublished) {
    return;
  }
  try {
    const body = new Blob([JSON.stringify({ force: true, source: "page_unload" })], { type: "application/json" });
    navigator.sendBeacon("/api/vision/stop", body);
    navigator.sendBeacon("/api/camera/stop", body);
    navigator.sendBeacon("/api/stop_voice_chat", body);
  } catch {
    // Page unload is best-effort; the Godot launcher also calls the bridge stop API.
  }
}

async function checkConfig() {
  try {
    const data = await fetchJson("/api/check_config");
    if (!data.issues.length) {
      log("StartVoiceChat config check passed.", "ok");
      return;
    }
    for (const issue of data.issues) {
      log(`[${issue.severity}] ${issue.key}: ${issue.message}`, issue.severity === "error" ? "bad" : "warn");
    }
  } catch (error) {
    log("Config check failed: " + error.message, "bad");
  }
}

function bindEngineEvents(engine) {
  const events = VERTC.events;
  const audioMediaType = MediaType.AUDIO;
  engine.on(events.onUserJoined ?? "onUserJoined", event => {
    logEvent("user_joined", event);
    postEvent("user_joined", event);
  });
  engine.on(events.onUserLeave ?? "onUserLeave", event => {
    logEvent("user_leave", event);
    postEvent("user_leave", event);
  });
  engine.on(events.onConnectionStateChanged, event => {
    logEvent("connection_state", event);
    postEvent("connection_state", event);
  });
  bindRtcEvent(engine, events.onLocalVideoSizeChanged ?? "onLocalVideoSizeChanged", "local_video_size_changed", event => {
    logEvent("local_video_size_changed", event);
    postEvent("local_video_size_changed", event);
  });
  bindRtcEvent(engine, events.onLocalStreamStats ?? "onLocalStreamStats", "local_stream_stats", stats => {
    if (stats?.isScreen || stats?.screenVideoStats || stats?.videoStats?.encodedFrameWidth) {
      postEvent("local_stream_stats", stats);
    }
  });
  engine.on(events.onUserPublishStream, async event => {
    logEvent("user_publish_stream", event);
    postEvent("user_publish_stream", event);
    if (isBotAudio(event)) {
      try {
        const userId = readRtcEventUserId(event);
        if (shouldMuteVolcRemoteAiAudio()) {
          await stopSubscribedBotAudio("bot audio publish while OmniVoice active");
          log("AI remote audio subscription skipped; OmniVoice handles local playback: " + userId, "ok");
          return;
        }
        await engine.subscribeStream(userId, audioMediaType);
        state.remoteBotAudioSubscriptions.add(userId);
        if (typeof engine.play === "function") {
          await engine.play(userId, audioMediaType);
        }
        log("Subscribed and playing AI audio: " + userId, "ok");
      } catch (error) {
        log("Subscribe/play AI audio failed: " + error.message, "warn");
      }
    } else {
      log("Remote publish ignored because it is not bot audio: " + compactJson(event), "warn");
    }
  });
  engine.on(events.onUserUnpublishStream ?? "onUserUnpublishStream", event => {
    logEvent("user_unpublish_stream", event);
    postEvent("user_unpublish_stream", event);
    const userId = readRtcEventUserId(event);
    if (state.remoteBotAudioSubscriptions.has(userId)) {
      state.remoteBotAudioSubscriptions.delete(userId);
    }
  });
  bindRtcEvent(engine, events.onRoomMessageReceived ?? "onRoomMessageReceived", "room_message", (...args) => {
    const event = normalizeTextMessageEvent(args);
    logEvent("room_message", event);
    postEvent("room_message", event);
  });
  bindRtcEvent(engine, events.onUserMessageReceived ?? "onUserMessageReceived", "user_message", (...args) => {
    const event = normalizeTextMessageEvent(args);
    logEvent("user_message", event);
    postEvent("user_message", event);
  });
  bindRtcEvent(engine, events.onRoomBinaryMessageReceived ?? "onRoomBinaryMessageReceived", "room_binary_message", (...args) => {
    const event = normalizeBinaryMessageEvent(args);
    const decoded = decodeRoomBinaryMessage(event.message);
    const payload = {
      userId: event.userId,
      tlvType: decoded.tlvType,
      text: decoded.text,
      json: decoded.json
    };
    logEvent("room_binary_message", payload);
    postEvent("room_binary_message", payload);
  });
  bindRtcEvent(engine, events.onUserBinaryMessageReceived ?? "onUserBinaryMessageReceived", "user_binary_message", (...args) => {
    const event = normalizeBinaryMessageEvent(args);
    const decoded = decodeRoomBinaryMessage(event.message);
    const payload = {
      userId: event.userId,
      tlvType: decoded.tlvType,
      text: decoded.text,
      json: decoded.json
    };
    logEvent("user_binary_message", payload);
    postEvent("user_binary_message", payload);
  });
  engine.on(events.onRemoteAudioPropertiesReport, event => {
    updateRemoteAiAudioEchoGuard(event);
    if (!shouldForwardVoiceEvent("remote_audio_properties_report")) {
      return;
    }
    postEvent("remote_audio_properties_report", event);
  });
  engine.on(events.onSubtitleMessageReceived, (...args) => {
    const event = normalizeSubtitleMessageEvent(args);
    logEvent("subtitle_message_received", event);
    postEvent("subtitle_message_received", event);
  });
  engine.on(events.onSubtitleStateChanged, event => {
    logEvent("subtitle_state_changed", event);
    postEvent("subtitle_state_changed", event);
  });
}

function normalizeTextMessageEvent(args) {
  if (args.length === 1 && args[0] && typeof args[0] === "object") {
    return args[0];
  }
  return {
    userId: args[0],
    message: args[1] ?? args[0] ?? ""
  };
}

function normalizeBinaryMessageEvent(args) {
  if (args.length === 1 && args[0] && typeof args[0] === "object" && "message" in args[0]) {
    return args[0];
  }
  return {
    userId: args[0],
    message: args[1]
  };
}

function normalizeSubtitleMessageEvent(args) {
  if (args.length === 1) {
    return args[0];
  }
  return args;
}

function bindRtcEvent(engine, eventName, logName, handler) {
  if (!eventName) {
    log("RTC event name missing: " + logName, "warn");
    return;
  }
  try {
    engine.on(eventName, handler);
  } catch (error) {
    log(`Bind RTC event failed ${logName}: ${error.message}`, "warn");
  }
}

function isBotAudio(event) {
  const mediaType = Number(event.mediaType);
  const hasAudio = mediaType === MediaType.AUDIO || (mediaType & MediaType.AUDIO) === MediaType.AUDIO;
  return readRtcEventUserId(event) === state.config.rtc.botUid && hasAudio;
}

function shouldMuteVolcRemoteAiAudio() {
  return Boolean(state.config?.voiceOutput?.muteVolcRemoteAiAudio);
}

async function stopSubscribedBotAudio(reason = "mute") {
  if (!state.engine || !state.remoteBotAudioSubscriptions.size) {
    return;
  }
  const audioMediaType = MediaType.AUDIO;
  for (const userId of Array.from(state.remoteBotAudioSubscriptions)) {
    if (typeof state.engine.stop === "function") {
      await tryCall(() => state.engine.stop(userId, audioMediaType), "Stop AI remote audio " + reason);
    }
    if (typeof state.engine.unsubscribeStream === "function") {
      await tryCall(() => state.engine.unsubscribeStream(userId, audioMediaType), "Unsubscribe AI remote audio " + reason);
    }
    state.remoteBotAudioSubscriptions.delete(userId);
  }
  log("AI remote audio subscriptions cleared (" + reason + ").", "ok");
}

function readRtcEventUserId(event) {
  if (typeof event === "string") {
    return event;
  }
  if (!event || typeof event !== "object") {
    return "unknown";
  }
  return event.userId || event.uid || event.userInfo?.userId || event.userInfo?.uid || "unknown";
}

function iterAudioReportItems(report) {
  if (!report) {
    return [];
  }
  if (Array.isArray(report)) {
    return report;
  }
  if (Array.isArray(report.audioPropertiesInfos)) {
    return report.audioPropertiesInfos;
  }
  if (Array.isArray(report.audioPropertiesInfo)) {
    return report.audioPropertiesInfo;
  }
  if (Array.isArray(report.audioInfos)) {
    return report.audioInfos;
  }
  if (Array.isArray(report.users)) {
    return report.users;
  }
  if (Array.isArray(report.remoteAudioPropertiesInfos)) {
    return report.remoteAudioPropertiesInfos;
  }
  if (typeof report === "object") {
    return [report];
  }
  return [];
}

function readAudioReportUserId(item) {
  if (!item || typeof item !== "object") {
    return "";
  }
  return String(
    item.userId
      || item.uid
      || item.user_id
      || item.userInfo?.userId
      || item.userInfo?.uid
      || item.streamKey?.userId
      || item.streamKey?.uid
      || ""
  );
}

function readAudioReportVolume01(item) {
  if (!item || typeof item !== "object") {
    return 0;
  }
  const raw = firstNumber(
    item.volume,
    item.linearVolume,
    item.audioPropertiesInfo?.volume,
    item.audioPropertiesInfo?.linearVolume,
    item.audioProperties?.volume,
    item.audioProperties?.linearVolume
  );
  if (!Number.isFinite(raw)) {
    return 0;
  }
  return raw > 1 ? raw / 255 : raw;
}

function firstNumber(...values) {
  for (const value of values) {
    const number = Number(value);
    if (Number.isFinite(number)) {
      return number;
    }
  }
  return NaN;
}

function decodeRoomBinaryMessage(message) {
  if (!message) {
    return { tlvType: "", text: "", json: null };
  }
  const bytes = new Uint8Array(message);
  if (bytes.length >= 8) {
    const type = new TextDecoder().decode(bytes.slice(0, 4));
    const size = new DataView(bytes.buffer, bytes.byteOffset + 4, 4).getUint32(0, false);
    const body = bytes.slice(8, Math.min(bytes.length, 8 + size));
    const text = new TextDecoder("utf-8").decode(body);
    return { tlvType: type, text, json: parseJson(text) };
  }
  const text = new TextDecoder("utf-8").decode(bytes);
  return { tlvType: "", text, json: parseJson(text) };
}

function parseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

async function postEvent(eventType, payload) {
  if (!shouldForwardVoiceEvent(eventType)) {
    return;
  }
  try {
    await fetchJson("/api/event", {
      method: "POST",
      body: JSON.stringify({
        event_type: eventType,
        trace_id: `web-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`,
        client_time: Date.now(),
        payload
      })
    });
  } catch (error) {
    log(`Event forward failed ${eventType}: ${error.message}`, "warn");
  }
}

function shouldForwardVoiceEvent(eventType) {
  const realtimeEvents = new Set([
    "remote_audio_properties_report",
    "subtitle_message_received",
    "room_binary_message",
    "user_binary_message",
    "room_message",
    "user_message",
    "function_call_event",
    "tool_call_event",
    "function_call",
    "tool_call",
    "ai_state_event",
    "conversation_state",
    "task_state"
  ]);
  if (!realtimeEvents.has(eventType)) {
    return true;
  }
  return state.eventForwardingActive && !state.stopping && state.voiceStarted;
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    }
  });
  const data = await response.json();
  if (!response.ok || data.ok === false) {
    throw new Error(data.error || response.statusText);
  }
  return data;
}

async function tryCall(fn, label) {
  try {
    await fn();
  } catch (error) {
    log(`${label} failed: ${error.message}`, "warn");
  }
}

function sleep(ms) {
  return new Promise(resolve => window.setTimeout(resolve, ms));
}

function setBusy(busy) {
  el.start.disabled = busy || state.voiceStarted;
  el.check.disabled = busy;
  updateVisionButtons();
  updateCameraButtons();
}

function updateVisionButtons() {
  const canRequest = Boolean(state.config) && state.visionSupported;
  el.visionStart.disabled = !canRequest || state.visionChanging || state.screenPublished;
  el.visionStop.disabled = state.visionChanging || (!state.visionDesired && !state.screenPublished);
  el.visionStart.classList.toggle("ready", canRequest && !state.screenPublished && !state.visionChanging);
  if (!state.visionDesired || state.screenPublished || !canRequest) {
    el.visionStart.classList.remove("attention");
  }
}

function updateCameraButtons() {
  const canRequest = Boolean(state.config);
  el.cameraStart.disabled = !canRequest || state.cameraChanging || state.cameraPublished;
  el.cameraStop.disabled = state.cameraChanging || (!state.cameraDesired && !state.cameraPublished);
  el.cameraStart.classList.toggle("ready", canRequest && !state.cameraPublished && !state.cameraChanging);
  if (!state.cameraDesired || state.cameraPublished || !canRequest) {
    el.cameraStart.classList.remove("attention");
  }
}

function setStatus(text, cls) {
  el.status.textContent = text;
  el.status.className = cls;
}

function logEvent(label, event) {
  log(`${label}: ${formatEventForLog(label, event)}`);
}

function log(message, cls = "") {
  const line = document.createElement("div");
  if (cls) {
    line.className = cls;
  }
  line.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
  el.log.appendChild(line);
  el.log.scrollTop = el.log.scrollHeight;
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(`[${cls || "info"}] ${message}`);
  }
  postClientLog(message, cls);
}

function postClientLog(message, cls = "") {
  fetch("/api/event", {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      event_type: "client_log",
      trace_id: `web-log-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`,
      client_time: Date.now(),
      payload: {
        level: cls || "info",
        message
      }
    })
  }).catch(() => {
    // Client logs are diagnostic only; never disturb the voice chain.
  });
}

function compactJson(value) {
  try {
    return JSON.stringify(value).slice(0, 900);
  } catch {
    return String(value);
  }
}

function formatEventForLog(label, event) {
  if ((label === "room_binary_message" || label === "user_binary_message") && event) {
    const userId = event.userId || "-";
    const tlvType = event.tlvType || "-";
    const json = event.json || {};
    const subtitle = firstSubtitleText(json);
    if (subtitle) {
      return `${userId} ${tlvType} subtitle="${subtitle}"`;
    }
    const stage = json.Stage?.Description || json.stage?.description || "";
    if (stage) {
      return `${userId} ${tlvType} stage=${stage}`;
    }
    if (json.Command) {
      return `${userId} ${tlvType} command=${json.Command}`;
    }
    return `${userId} ${tlvType}`;
  }
  return compactJson(event);
}

function firstSubtitleText(json) {
  const data = Array.isArray(json?.data) ? json.data : [];
  for (const item of data) {
    const text = String(item?.text || "").trim();
    if (text) {
      return text.slice(0, 120);
    }
  }
  return "";
}

function safeSdkVersion() {
  try {
    return VERTC.getSdkVersion();
  } catch {
    return "unknown";
  }
}
