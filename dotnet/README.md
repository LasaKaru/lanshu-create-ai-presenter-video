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
        ↓  review:    optional — read and edit the narration before it is spoken
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

## Review the script before it is spoken

Narration is the master clock, so wording is settled before anything is synthesized. Tick
**Review the script before it is spoken** when creating a job (or `--review-script` on the CLI,
or turn it on for every job in Settings) and the run pauses after drafting.

The studio then shows an editor: per-beat role, title, callout keyword and the spoken narration,
with a live estimate of how long it will actually take to say against your target duration. Pasted
markdown is reduced to speakable words, and every beat is given a callout keyword if you leave one
blank. Save and approve, and the run continues.

Editing the wording of a job whose audio is already locked **discards that narration on purpose**.
Captions, keyword anchors, chapter boundaries and every cut are timed against the old recording,
so it is re-spoken rather than left out of sync. Changing only a title or a keyword leaves the
audio alone.

## Fast preview

A full delivery render does two encodes, a loudness pass, a contact sheet and the acceptance
gates. When you only need to see whether the captions and callouts land in the right place, use
**Fast preview** (or `lanshu run --preview`). It renders a small proxy at the same aspect ratio
with an ultrafast preset and stops there — roughly five times quicker on a 45-second video.

Caption and callout geometry is authored against the delivery resolution and scaled by libass, so
what you see in the proxy is what the master will look like. The preview never advances the job
state or overwrites the accepted presenter plate.

Set the proxy's short edge in Settings; 640 is the default.

## Voice cloning

When a job carries an authorized voice sample and the speech engine supports cloning, the sample
is uploaded once and the resulting voice is used for the whole narration. A voice is a person's
likeness, so this only happens when **both** approvals are on the job: `voice_clone_approved` (the
sample owner's permission) and `remote_upload_approved`. Missing either, the run says why in its
warnings and falls back to a stock voice rather than uploading anything.

The created voice id is recorded on the job, and the run tells you a voice now exists in your
provider account so you can delete it when the job is done. ElevenLabs is the engine in this set
with a voice-add endpoint; others report that they cannot clone rather than failing the run.

## Narration reuse and per-segment re-takes

A re-run only speaks what changed. Segments whose script wording is unchanged and whose audio still
exists are reused, so a second render costs nothing on a paid engine. Changing the voice provider
re-speaks everything, because mixing two voices in one narration is never what you want.

To fix a single sentence — a mispronounced name, an odd emphasis — re-take just that segment:

```bash
lanshu segments --job-dir <dir>
lanshu retake  --job-dir <dir> --index 3 --say "measured wunce, respected everywhere"
lanshu run     --job-dir <dir>
```

`--say` changes only **what reaches the speech engine**. Captions and keyword anchors keep the
script's wording, so a respelling fixes the audio without corrupting what the viewer reads. Omit
`--say` to re-speak the script wording as-is; `--clear-say` removes an existing respelling.

Re-taking shifts every later segment's start, so the assembled narration, captions and render are
rebuilt — but the other segments' audio is kept, which is the point.

## Lip-sync

The motion plate has no mouth movement of its own. When a lip-sync tool is available the pipeline
applies it to the accepted plate using the exact locked narration, without extending the take —
the repair described in `qa-recovery.md`. A locally installed tool is preferred over a paid
endpoint; if neither is configured, or the repair fails, the plate is kept and the job record says
so rather than claiming sync it does not have.

Local tools are configured as a command template, so Wav2Lip, SadTalker, video-retalking or
anything else with a CLI works with no code change:

```json
{
  "command": "python",
  "arguments": "inference.py --checkpoint_path {{CHECKPOINT}} --face {{VIDEO}} --audio {{AUDIO}} --outfile {{OUTPUT}}",
  "working_directory": "/path/to/Wav2Lip",
  "checkpoint_path": "/path/to/wav2lip_gan.pth"
}
```

Placeholders: `{{VIDEO}}` `{{AUDIO}}` `{{IMAGE}}` `{{OUTPUT}}` `{{OUTPUT_DIR}}` `{{CHECKPOINT}}`.
Arguments are split on whitespace *before* substitution, so a path containing spaces stays one
argument. Nothing is downloaded automatically — these tools carry multi-gigabyte weights and their
own licences, so you install one and point the studio at it.

Which generator produced the motion and which tool synced the mouth are recorded as separate
capabilities, because they are separate facts.

`lanshu lipsync` removes the fiddly part — knowing the right command line for the tool you
installed, and finding out whether it actually runs *before* a real job depends on it:

```bash
lanshu lipsync --list                              # the tools with known command lines
lanshu lipsync --use wav2lip --dir ~/src/Wav2Lip   # fills in the command, args and checkpoint
lanshu lipsync --test                              # PASS  wav2lip produced 2.00s of video
```

`--test` builds a two-second synthetic face and tone, runs the configured tool over them, and
reports the duration of what came back. A tool that writes nothing, writes a still, or exits
non-zero fails here rather than half way through a paid run. `--use` only fills in the command
template; the weights stay yours to install.

## Background replacement

A presenter shot against a green screen, a messy room, or a wall that fights the caption colour
can be cut out and re-backed before any motion is applied, so the plate, the punch-ins and the
lip-sync repair all work on the composited still rather than around it.

| Mode | What it needs |
|---|---|
| `chroma` | a green or blue screen; `chromakey` plus `despill` so the spill does not tint skin |
| `matte` | a greyscale matte you supply — white keeps, black drops |
| `cutout` | any tool that writes an RGBA PNG, e.g. `rembg`, as `{{INPUT}}` → `{{OUTPUT}}` |

Backdrops are a blurred copy of the original frame, a solid colour, a two-stop gradient, or an
image of your own. Replacement is best-effort by design: if the key, the matte or the external
tool fails, the run keeps the original image and says so in its warnings instead of shipping a
half-cut presenter.

## Emphasis punch-ins

Each keyword callout also gets a small eased push of the frame, bound to the same spoken anchor, so
an emphasis beat reads visually as well as textually. Pushes that would overlap are dropped rather
than stacked — a frame that never settles reads as a wobble instead of emphasis. Turn them off with
`--no-punch-ins`.

## Motion that follows the voice

The plate's sway is driven by the narration itself rather than by a fixed sine wave: the audio is
decoded once to a per-frame loudness envelope, smoothed with a fast attack and a slow release so
the frame leans into a phrase and settles out of it, and normalized against the 95th percentile
rather than the peak — one loud consonant should not define the whole take's range.

Only the crop's `x` and `y` are commanded per frame, through an ffmpeg `sendcmd` script. Changing
the crop's width or height mid-stream would change the output frame size, which the encoder cannot
accept, so the zoom stays on `zoompan` and the reaction rides the pan. Turn it off, or change how
far it leans, with `audio_reactive` and `audio_weight` in Settings.

## Multi-shot framing

A single unbroken framing for a minute of narration reads as a webcam. The chapter times are
already measured, so each chapter is given a framing — wide, medium or close — and the plate cuts
between them on chapter boundaries:

- the hook and the close stay **wide**, because that is where the whole frame matters;
- a chapter under two and a half seconds stays wide rather than flashing a push;
- no two neighbouring chapters share a framing, so every boundary is visible as a cut.

A closer shot also raises the crop, since a tighter frame on a standing subject should hold the
face rather than the chest. Punch-ins compose on top of the shot's scale, so an emphasis beat
inside a close shot still reads as a push rather than jumping back to wide.

## Multi-aspect delivery and the publishing kit

`--also-aspect 16:9 --also-aspect 1:1` delivers extra ratios from the same locked narration. The
presenter plate and caption timings are reused unchanged; only the frame geometry and the caption
layout are rebuilt, because callouts and captions sit in different safe regions per orientation. So
every version stays on one clock and each gets its own master, share copy, contact sheet and QA.

Alongside the video you get three thumbnail variants — accent bar, top scrim, and an oversized
stamp — rendered from a **clean frame of the presenter plate** rather than the delivered cover,
which already carries burned-in captions.

Chapter markers and a description are written from the measured chapter times. Markers are only
emitted when they satisfy YouTube's rules (first at 0:00, at least three, each at least ten
seconds); otherwise the file explains why and lists them commented out, because a chapter list that
breaks those rules is silently ignored and looks like a fault in the video rather than the
metadata.

## Hardware encoding

Renders use NVENC, Quick Sync, VideoToolbox or AMF when one of them actually works on the machine,
and libx264 otherwise. Being listed by `ffmpeg -encoders` is not proof — a build can advertise
NVENC on a machine with no NVIDIA card — so each candidate is proven once by encoding a few
synthetic frames before it is trusted.

A hardware encoder can also pass that probe and still fail mid-run on a busy GPU or a driver
reset. When that happens the encode is retried in software automatically rather than failing the
job. The encoder that actually ran is recorded on the job under `capabilities.encoder_qa`.

The Environment tab reports the resolved encoder and every hardware encoder that passed its probe.

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
dotnet test LanshuPresenter.sln          # 121 tests
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

Add `--preview` to `run` for the fast proxy, and `--review-script` to `init` to pause for a script
review (approve it with `lanshu approve --script`). `--also-aspect` is repeatable;
`--no-punch-ins` and `--no-publishing-kit` turn off the extras.

`lanshu pilot` shows a pilot's real duration, format and provider, and lays its frames out as a
contact sheet so it can be reviewed without a video player; `--open` launches it in the default
player.

Other commands: `preflight`, `approve`, `finalize`, `segments`, `retake`, `pilot`, `lipsync`,
`voices`, `jobs`, `config`, `version`.
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
- `PublishSingleFile` and `SelfContained` are passed on the publish command rather than set in the
  project files, because setting them there forces a runtime identifier onto plain `dotnet build`
  and moves its output directory.
