# User Setup Notice

This project does not commit real voice, RTC, or LLM credentials.

Local runtime configuration is based on example files in:

```text
config/
unity/SilverWolfPet/Assets/StreamingAssets/GodotFinal/config/
```

Copy the matching example file to a `.local.json` file on your own machine, then fill in your own values. `.local.json` files are ignored by Git.

## Common Files

```text
volc_start_voice_chat.example.json
volc_traditional_voice_chat.example.json
volc_traditional_companion_polling.example.json
agent_speaker.example.json
omnivoice_gateway.example.json
pet_memory.example.json
```

## Safety Rules

- Do not commit `.local.json` files.
- Do not commit `.env` files.
- Do not commit RTC tokens, OpenAPI keys, cloned voice keys, or third-party API keys.
- Re-run `python scripts/check_secret_hygiene.py` before pushing.

## Face Tracking

The scene version can launch the MediaPipe tracker from:

```text
head_tracker/head_tracker.py
unity/SilverWolfPet/Assets/StreamingAssets/head_tracker/head_tracker.py
```

Install dependencies locally:

```powershell
python -m pip install -r head_tracker/requirements.txt
```

The tracker uses UDP port `5055` and the scene camera frame server uses TCP port `17863` by default.
