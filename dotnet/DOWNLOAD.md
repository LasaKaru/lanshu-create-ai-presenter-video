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

  From a command prompt, "lanshu lipsync --list" shows the tools it already
  knows the command line for, "lanshu lipsync --use wav2lip --dir <folder>"
  fills that command line in, and "lanshu lipsync --test" proves the tool
  really runs on a two-second test clip before a real job depends on it.

CHANGING THE BACKGROUND

  Shot against a green screen, or a wall that fights the captions? The
  studio can cut the presenter out and put them on a blurred, solid,
  gradient or custom backdrop before any motion is applied. Give it a green
  screen, a black-and-white matte of your own, or a cutout tool such as
  rembg. If the cutout fails it keeps your original picture and tells you,
  rather than shipping a half-cut presenter.

HOW THE PICTURE MOVES

  The frame leans into the narration instead of drifting on a timer - the
  loudness of the voice drives the sway, so it moves with the sentence and
  settles at the end of it.

  Each section of the script also gets its own framing: wide, medium or
  close, cutting on the section boundaries, with the opening and closing
  sections kept wide. One unbroken framing for a whole minute looks like a
  webcam; this looks edited.

A LOOK YOU CAN REUSE

  Save the colours, font, caption style, watermark and title cards of a
  video you liked as a "brand kit", then apply it to the next one by name
  instead of setting it all up again.

CAPTION STYLES

  Four to choose from: a plain readable line, the same on a solid plate for
  busy footage, a karaoke sweep that follows the voice, and a TikTok-style
  one or two words at a time in large type.

  The karaoke sweep needs real measured word timings (an ASR key in
  Settings). Without them it quietly uses the plain style rather than
  sweeping out of time with the voice.

TITLE AND END CARDS

  Give it a title and a subtitle and it puts a card on the front and back
  of the video. The cards are added after the edit, so nothing else moves -
  captions, callouts and the chapter list all stay where the voice put them.

CHOOSING WHAT APPEARS WHEN

  By default your extra pictures and clips are spread across the middle
  sections. Press "Timeline" on a job to see the narration drawn as a
  waveform with everything laid out against it, and drag a picture to where
  you actually want it - or its edge to change how long it stays.

TIGHTER, CLEANER NARRATION

  Dead air at the start and end of each spoken line is trimmed off, and
  filler words are dropped from the script before it is ever spoken, so the
  captions never show a word the voice does not say.

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
