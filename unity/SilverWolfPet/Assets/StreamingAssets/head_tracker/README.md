# UVC Head Tracker for Unity Parallax

This is the Python side of the Unity head-parallax prototype. It reads a normal UVC webcam, tracks one face with MediaPipe Face Mesh, estimates a pseudo depth value from face width, and sends low-latency UDP JSON to `127.0.0.1:5055`.

## Install

```powershell
cd D:\pet\head_tracker
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
```

This prototype is tested with Python `3.12.10` and MediaPipe `0.10.35`. Newer MediaPipe builds use the Tasks API instead of the old `mp.solutions.face_mesh` API, so this repo includes support for both.

The tested Face Landmarker model is:

`D:\pet\head_tracker\models\face_landmarker.task`

## Run

```powershell
cd D:\pet\head_tracker
.\.venv\Scripts\activate
python .\head_tracker.py --preview
```

Press `q` or `Esc` in the preview window to quit.

## UDP JSON

Each frame sends JSON like:

```json
{
  "face_found": true,
  "face_center_x": -0.12,
  "face_center_y": 0.04,
  "face_width_px": 178.5,
  "yaw": -8.0,
  "pitch": 2.0,
  "roll": 1.5,
  "z_cm": 57.1,
  "z_offset": 0.05,
  "timestamp": 1777520000.0
}
```

`face_center_x` and `face_center_y` are normalized around screen center in the range `[-1, 1]`. `z_offset` is positive when your face is closer than the calibrated default distance.

## Calibration

`baseline_face_width_px` is the face bounding-box width, in pixels, at `default_distance_cm`.

1. Sit at the distance you want to be neutral, for example `60 cm`.
2. Run with preview:

```powershell
python .\head_tracker.py --preview --default-distance-cm 60
```

3. Keep your head centered and press `c`. The console prints a value like:

   ```text
   baseline_face_width_px=173.4
   ```

4. Use that value next time:

   ```powershell
python .\head_tracker.py --preview --baseline-face-width-px 173.4 --default-distance-cm 60
```

For a non-interactive smoke test that exits automatically:

```powershell
python .\head_tracker.py --backend tasks --max-frames 60 --print-every 15
```

If the Z motion feels too strong in Unity, reduce `gainZ` on `HeadDrivenCamera`. If it feels too twitchy, lower Python `--cutoff-hz` or raise Unity `smoothing`.

## Low Latency Notes

- Default capture is `640x480 @ 30 fps`.
- OpenCV uses `CAP_DSHOW` on Windows to reduce camera startup and buffering issues.
- The script requests `CAP_PROP_BUFFERSIZE = 1`; some webcam drivers ignore it.
- Preview rendering costs a little latency. For final testing, run without `--preview`.
