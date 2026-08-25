# Lanshu AI Presenter Studio (.NET)

A Windows-first desktop application that runs the same presenter-video workflow as the
Codex skill in the repository root. Give it a script or a topic plus one authorized
presenter image and it produces a finished, verified video: narration, captions, keyword
callouts, an animated or lip-synced presenter track, a master and share encode, a contact
sheet, and a QA report.

It ships as a **single self-contained executable**. No .NET runtime, no installer.

```
LanshuPresenterStudio.exe   the studio (opens a local UI in your browser)
lanshu.exe                  the same pipeline as a CLI
```

## What it does

```text
topic or script + authorized presenter image
        ↓  preflight: decode inputs, check approvals and manual review
        ↓  content:   write or parse the script into beats with callout keywords
        ↓  audio:     synthesize the complete narration, one voice, one configuration
        ↓  timings:   transcribe or align every word against the measured audio
        ↓  presenter: talking-head provider, or a local animated motion plate
        ↓  timeline:  deterministic clips, inserts, captions, callouts, progress line
        ↓  render:    one ffmpeg composition pass driven by the locked narration
        ↓  deliver:   two-pass loudness, master + share, full decode, contact sheet
        ↓  verify:    machine acceptance gates plus an explicit visual review list
master.mp4 · share.mp4 · cover.png · contact-sheet.png · captions.srt · QA report
```

The complete narration is the master clock. Presenter motion, captions, callouts, chapter
boundaries and the delivered duration are all positioned against that one measured file,
which is what keeps lip timing and segment joins from drifting.

## It works with nothing configured

On a clean machine with no API keys and no network:

- **Script** — the built-in outline writer builds the hook/beats/close structure from your
  topic. It does not invent facts; you edit the narration before locking audio. Add an
  Anthropic or OpenAI key for a fully drafted script.
- **Voice** — Windows system voices via SAPI. `espeak-ng` on Linux, `say` on macOS.
- **Word timings** — the proportional aligner. Because narration is synthesized one segment
  at a time, the exact text and the exact measured duration of each segment are already
  known; only the position of each word inside its own segment is estimated.
- **Presenter track** — a local motion plate: a slow eased Ken Burns push, a breathing sway,
  a blurred backdrop when the source aspect does not match the delivery aspect, plus a light
  grade and vignette.

The motion plate is **not** a lip-synced talking head. Every job record, QA report and run
summary states which generator actually produced the track, so the output is never described
as something it is not. Configure a talking-head provider for real mouth synchronization.

## Optional providers

| Capability | Providers |
|---|---|
| Script writing | Anthropic (`claude-opus-5` by default), any OpenAI-compatible chat endpoint |
| Speech | ElevenLabs, OpenAI, Azure Speech, Piper, Windows SAPI, macOS `say`, espeak-ng |
| Word-timestamp ASR | OpenAI Whisper, local whisper.cpp, or the built-in aligner |
| Talking head / lip-sync | any submit-poll-download HTTP API you describe in Settings |

No vendor is hard-coded for video. You supply the submit URL, a JSON request template with
placeholders, and the JSON paths to read the task id, status and result URL back out:

```json
{
  "model": "{{MODEL}}",
  "image": "{{IMAGE_DATA_URI}}",
  "audio": "{{AUDIO_DATA_URI}}",
  "prompt": "{{PROMPT}}",
  "negative_prompt": "{{NEGATIVE_PROMPT}}",
  "width": {{WIDTH}}, "height": {{HEIGHT}}, "fps": {{FPS}}
}
```

Available placeholders: `{{IMAGE_DATA_URI}}` `{{AUDIO_DATA_URI}}` `{{VIDEO_DATA_URI}}`
`{{IMAGE_BASE64}}` `{{AUDIO_BASE64}}` `{{VIDEO_BASE64}}` `{{PROMPT}}` `{{NEGATIVE_PROMPT}}`
`{{WIDTH}}` `{{HEIGHT}}` `{{FPS}}` `{{DURATION}}` `{{DURATION_INT}}` `{{MODEL}}` `{{VERSION}}`.

## Safety and cost gates

These are enforced by the code, not left to the operator:

- Preflight blocks every render until the presenter image has been viewed, confirmed to hold
  one clear adult face, and confirmed free of unwanted text. A voice sample additionally
  requires a listened-to confirmation and one clear speaker.
- Remote generation is blocked until image rights, adult status and upload approval are all
  recorded on the job. Without them the run falls back to the local motion plate rather than
  uploading anything.
- Before the first paid call the run **stops** and reports what would be uploaded, how many
  seconds are requested, the known price and its evidence date, the pilot size, and the retry
  ceiling. Nothing is spent until you approve that specific plan.
- A short pilot is generated before any full paid run, and the run stops again for you to
  watch it.
- After three rejected paid candidates the run stops and summarizes rather than continuing to
  spend.
- Credentials live in `~/.lanshu-presenter/secrets.json` (user-only permissions on Unix) and
  are never written into a job folder. Archived provider requests have credentials redacted,
  embedded media replaced with a size marker, and signed-URL query strings stripped.
- Reports store filenames and job-relative paths only, so a shared QA report never leaks a
  machine path.

## Install

Download the archive for your platform from the
[Releases page](https://github.com/LasaKaru/lanshu-create-ai-presenter-video/releases),
unzip it, and run `LanshuPresenterStudio.exe`.

Every push also produces a build on the
[Actions page](https://github.com/LasaKaru/lanshu-create-ai-presenter-video/actions)
under the run's **Artifacts** section.

### FFmpeg

The studio needs `ffmpeg` and `ffprobe`. If they are not on the machine, open the
**Environment** tab and press **Install FFmpeg** — it downloads a portable build into
`~/.lanshu-presenter/tools` without touching anything system wide. The CLI equivalent is
`lanshu doctor --install`.

### Build from source

```bash
cd dotnet
dotnet test LanshuPresenter.sln          # 42 tests
./build/publish.sh win-x64               # or linux-x64, osx-arm64, ...
```

`build/publish.ps1` is the PowerShell equivalent. Windows builds cross-compile from Linux
and macOS via `-p:EnableWindowsTargeting=true`, which the scripts already pass.

## Using the studio

1. **Create** — topic or script, presenter image, voice, format, optional supporting media.
2. Tick the manual review boxes. Preflight refuses to render without them.
3. **Create and render**. Progress streams live; the finished video plays in place with
   download links for the master, share copy and captions.
4. **Jobs** — every job's state, artifacts, script, beat sheet, timeline, QA report and run
   log. Re-running resumes from the last completed stage rather than starting over.

## Using the CLI

```bash
lanshu doctor --install

lanshu init --topic "Explain context engineering in one minute" \
            --presenter-image ./presenter.png \
            --duration 60 --aspect 9:16 \
            --reviewed --rights-confirmed --adult-presenter-confirmed

lanshu run --job-dir ~/.lanshu-presenter/jobs/explain-context-engineering-20260825-1200
```

Other commands: `preflight`, `approve`, `finalize`, `voices`, `jobs`, `config`, `version`.
`lanshu` with no arguments prints the full reference.

`lanshu run` exits `0` when the acceptance gates pass, `1` when a gate fails, `3` when it is
waiting on an approval, and `2` on error — so it drops straight into a script or CI job.

## Job layout

A job directory is interchangeable with the Python skill's — the `job.json` wire format is
identical, so a job created by either tool can be resumed by the other.

```text
job.json
docs/{SCRIPT,BEAT_SHEET,TIMELINE}.md · docs/{script,timeline}.json
assets/source/ · assets/audio/{reference,raw,final}/
assets/video/{candidates,selected,render}/ · assets/captions/
qa/{requests,asr,contacts,reports}/
renders/ · outputs/
```

## Layout of the source

```text
dotnet/
├── src/Lanshu.Presenter.Core/     the whole pipeline, no UI dependency
│   ├── Models/ Jobs/              job manifest, state machine, artifact tree
│   ├── Environment/ Media/        tool discovery, FFmpeg install, ffprobe, loudness
│   ├── Preflight/ Content/        input gates, script writing and parsing
│   ├── Voice/ Asr/ Captions/      narration, word timings, SRT and ASS with presets
│   ├── Presenter/ Timeline/       generators, remote job client, compositor
│   ├── Delivery/ Qa/              master and share encodes, acceptance gates
│   └── Pipeline/                  the state-machine runner
├── src/Lanshu.Presenter.Cli/      the lanshu command
├── src/Lanshu.Presenter.App/      the studio: minimal API + embedded UI
└── tests/Lanshu.Presenter.Tests/
```

## Notes

- The studio binds to `127.0.0.1` on a free port and requires a per-session token that is
  printed in the console, so another local process cannot drive its file and render APIs.
- Keyword callouts rotate through six presets (radial burst, tilted ribbon, marker circle,
  type contrast, word chips, double outline) so no single card repeats through a video, and
  each one binds to a real spoken anchor.
- If the FFmpeg build cannot burn in subtitles, captions are still delivered as a sidecar
  `.srt` and the environment report says so rather than failing the render.
