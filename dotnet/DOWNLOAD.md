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

WHAT YOU GET

  A master MP4, a smaller share copy, a cover frame, a nine-frame contact
  sheet, an .srt caption file, and a QA report, all under
  %USERPROFILE%\.lanshu-presenter\jobs\<job>\outputs.

WITHOUT ANY API KEY

  Everything runs locally: Windows system voices speak the narration,
  and the presenter track is an animated motion plate rendered from your
  still image. It is not a lip-synced talking head, and the job record
  always says which one produced the track.

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
