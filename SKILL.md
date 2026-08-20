---
name: lanshu-create-ai-presenter-video
description: Turn a topic or finished script plus an authorized adult presenter image into a complete, publish-ready AI presenter video. Use for new presenter videos and for continuing, revising, captioning, lip-sync repairing, or re-exporting an existing presenter-video job. Keep model and provider selection capability-based and record the actual choices per job.
---

# Lanshu Create AI Presenter Video

Produce a verified presenter-led video from minimal inputs. The final narration is the master clock for presenter motion, captions, graphics, cuts, and delivery duration.

## Required outcome

- Start from a topic or script and one authorized image containing one clear adult presenter.
- Deliver a fully decoded master video, a smaller share copy, captions, production records, and separate machine and visual QA notes.
- Keep the workflow portable across providers, models, aspect ratios, languages, and durations.

## Operating rules

- Confirm image rights, adult status, remote-upload approval, and voice-cloning authorization before the relevant remote action.
- Treat remote generation as potentially billable. Before the first paid call, state the uploaded assets, requested seconds or units, known cost, pilot size, retry ceiling, and expected output. Reuse an approval already given for that exact plan.
- Never infer a real voice from an image. Use an authorized sample or record a selected stock voice.
- Lock the complete narration before presenter generation, caption timing, or final scene boundaries.
- Prefer one presenter image, one voice identity, one visual treatment, and one continuous presenter source.
- Mute video sources in the final composition. Route only the approved external narration and intentional mix tracks.
- Preserve provider request bodies and task IDs without credentials or expiring URLs. Poll interrupted work before considering resubmission.
- Use numeric checks for technical faults and normal-speed visual review for identity, mouth timing, blinking, gestures, hands, lighting, and continuity.
- Stop after three rejected paid candidates, or before a change that materially affects cost, privacy, voice, appearance, or provider.
- Do not claim completion until the final files fully decode and the contact sheet or full playback has been reviewed.

## Start or resume a job

For a new job, read [generation.md](references/generation.md), then run:

```bash
SKILL_DIR=~/.codex/skills/lanshu-create-ai-presenter-video

python3 "$SKILL_DIR/scripts/init_job.py" \
  --job-dir ~/Videos/my-presenter-video \
  --presenter-image ~/Pictures/presenter.png \
  --topic "用一分钟讲清楚上下文工程"
```

Use `--script` for an existing script file. Optional flags include `--voice-sample`, `--supporting-media`, `--duration`, `--aspect`, `--width`, `--height`, `--fps`, `--watermark`, and `--cta`.

Inspect the actual source image and listen to any voice sample. Record the manual review and approvals in `job.json`, then run:

```bash
python3 "$SKILL_DIR/scripts/preflight.py" ~/Videos/my-presenter-video/job.json
```

For an existing job, read `job.json`, current artifacts, task IDs, and QA reports. Resume from the earliest unfinished state without regenerating accepted work.

## Production state machine

Advance a job only when its evidence exists:

```text
intake
→ content_locked
→ audio_locked
→ visual_plan_locked
→ presenter_generated
→ composition_checked
→ rendered
→ verified
```

### 1. Lock content and audio

Read [generation.md](references/generation.md).

1. Turn a topic into one spoken content spine, or polish a supplied script without changing factual meaning silently.
2. Save the production script, beat sheet, pronunciations, and narration sections.
3. Generate the complete approved narration with one voice configuration.
4. Normalize sections consistently, run ASR on the final audio, and correct material omissions, additions, numbers, names, or repeated speech.
5. Record real durations. These durations now define the timeline.

### 2. Plan and generate the presenter

Choose a presenter-led, screen-demo, or mixed-explainer route. Use supporting media only when it proves or clarifies a spoken point.

Generate a short, low-cost pilot before a full run. Prioritize identity and mouth timing for the main track. Use one controlled gesture for an actionful opening or close. When body motion is accepted but mouth timing fails, preserve the motion plate and apply a dedicated lip-sync repair with the locked audio.

Archive the prompt, parameters, provider/model/version, region, task ID, requested seconds, and acceptance notes.

### 3. Edit the video

Read [editing.md](references/editing.md).

Build a deterministic timeline driven by the locked audio. Keep authored start, duration, and source offset independent. Add captions and keyword callouts only after audio and selected media are final.

When using HyperFrames, load the current `hyperframes`, `general-video`, and relevant domain instructions. Run the project check, inspect transition and emphasis frames, open the Studio preview, and wait for final visual approval before rendering.

### 4. Verify and deliver

Read [qa-recovery.md](references/qa-recovery.md).

Render at delivery quality, watch the complete video, and finalize it:

```bash
bash "$SKILL_DIR/scripts/finalize_delivery.sh" \
  ~/Videos/my-presenter-video/renders/rendered.mp4 \
  ~/Videos/my-presenter-video/outputs \
  my-video
```

The finalizer preserves the input aspect ratio, performs two-pass program loudness normalization, creates master/share encodes, fully decodes them, writes probes and a delivery report, and produces a nine-frame contact sheet. Inspect that sheet before handoff.

## Default behavior for minimal input

- Infer language from the request.
- Use 9:16, 1080×1920, 30fps unless the intended platform suggests another format.
- Preserve a supplied script's natural duration; for a topic, target 45–75 seconds.
- Use a suitable stock voice when no authorized voice sample exists.
- Use a presenter-led layout with a designed hook, 2–4 useful beats, and a concise close.
- Omit music and promotional CTA unless requested or clearly justified.
- Keep styling credible, contemporary, readable, and safe for the destination platform.

## Required job artifacts

```text
job.json
docs/{SCRIPT,BEAT_SHEET,TIMELINE,STORYBOARD}.md
assets/source/
assets/audio/{reference,raw,final}/
assets/video/{candidates,selected,render}/
assets/captions/
qa/{requests,asr,contacts,reports}/
renders/
outputs/
```

## Reference routing

- Read [generation.md](references/generation.md) for intake, content, voice, tool selection, presenter prompts, paid generation, or provider changes.
- Read [editing.md](references/editing.md) for timeline construction, screen-demo layouts, openings, closes, captions, keyword callouts, previews, and exports.
- Read [qa-recovery.md](references/qa-recovery.md) before accepting media or delivery, and whenever lip sync, identity, hands, exposure, freezes, captions, audio, or remote jobs fail.
