Lanshu AI Presenter Studio
==========================

WHAT IS IN THIS FOLDER

  LanshuPresenterStudio.exe   The studio. Double-click it.
  lanshu.exe                  The same pipeline as a command-line tool.

Both are self-contained: no .NET runtime, no installer, nothing to set up.

FIRST RUN

  1. Double-click LanshuPresenterStudio.exe.
     A console window opens and your browser opens the studio.
     Keep the console window open while you work. Close it to stop.

  2. Open the Environment tab and press "Install FFmpeg" if it reports
     FFmpeg as missing. It downloads a portable build into
     %USERPROFILE%\.lanshu-presenter\tools and nothing is installed
     system wide.

  3. Go to Create, give it a topic or paste a script, choose a presenter
     image, tick the review boxes, and press "Create and render".

     "Fast preview" renders a small proxy in a fraction of the time so you
     can check the captions and callouts before committing to a full render.

     Tick "Review the script before it is spoken" to read and edit the
     narration first. Nothing is synthesized until you approve it.

WHAT YOU GET

  A master MP4, a smaller share copy, a cover frame, a nine-frame contact
  sheet, an .srt caption file, and a QA report, all under
  %USERPROFILE%\.lanshu-presenter\jobs\<job>\outputs.

SPEED

  Renders use your GPU (NVENC, Quick Sync, VideoToolbox or AMF) when one is
  available and working, and fall back to software encoding otherwise. The
  Environment tab shows which one was chosen.

WITHOUT ANY API KEY

  Everything runs locally: Windows system voices speak the narration,
  and the presenter track is an animated motion plate rendered from your
  still image. It is not a lip-synced talking head, and the job record
  always says which one produced the track.

EXTRAS IN EVERY RENDER

  Three thumbnail variants, chapter markers, a description, and a small
  emphasis push on each keyword beat. Ask for extra aspect ratios and they
  are delivered from the same narration, so a 9:16 and a 16:9 cut stay in
  sync with each other.

FIXING ONE SENTENCE

  If a single line sounds wrong, re-take just that segment instead of
  re-rendering everything - and give the engine a respelling if it
  mispronounced a name. The captions keep the real spelling.

REAL LIP-SYNC

  Install Wav2Lip, SadTalker or video-retalking and point the studio at it
  in Settings; the presenter's mouth then follows the narration. Nothing is
  downloaded for you - those tools are large and carry their own licences.

WITH API KEYS (Settings tab)

  Anthropic or OpenAI   full script drafting from a topic
  ElevenLabs / OpenAI / Azure   higher quality voices
  OpenAI or whisper.cpp   measured word timings for captions
  Any talking-head API   a real lip-synced presenter

  Keys are stored in %USERPROFILE%\.lanshu-presenter\secrets.json and are
  never written into a job folder, a QA report, or an archived request.

WINDOWS SMARTSCREEN

  The executable is not code-signed, so SmartScreen may warn on first run.
  Choose "More info" then "Run anyway", or build it yourself from source.

MIT licensed. https://github.com/LasaKaru/lanshu-create-ai-presenter-video
