'use strict';

const $ = (id) => document.getElementById(id);
const state = {
  settings: null,
  uploads: { presenterImage: '', voiceSample: '', scriptFile: '', music: '', supporting: [] },
  selectedJob: '',
  runSource: null,
  voiceGroups: [],
};

/* ------------------------------------------------------------------ plumbing */

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: 'same-origin',
    ...options,
    headers: { ...(options.body instanceof FormData ? {} : { 'Content-Type': 'application/json' }), ...(options.headers || {}) },
  });
  const text = await response.text();
  let payload = null;
  try { payload = text ? JSON.parse(text) : null; } catch { payload = { raw: text }; }
  if (!response.ok) throw new Error((payload && payload.error) || `request failed (${response.status})`);
  return payload;
}

function toast(message, ms = 3200) {
  const element = $('toast');
  element.textContent = message;
  element.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { element.hidden = true; }, ms);
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

const fileUrl = (path, download) =>
  `/api/file?path=${encodeURIComponent(path)}${download ? '&download=1' : ''}`;

async function upload(file) {
  const form = new FormData();
  form.append('file', file, file.name);
  const result = await api('/api/upload', { method: 'POST', body: form });
  return result.path;
}

/* ---------------------------------------------------------------------- tabs */

document.querySelectorAll('.tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach((other) => other.classList.toggle('is-active', other === tab));
    document.querySelectorAll('.panel').forEach((panel) =>
      panel.classList.toggle('is-active', panel.id === `tab-${tab.dataset.tab}`));
    if (tab.dataset.tab === 'jobs') loadJobs();
    if (tab.dataset.tab === 'environment') loadEnvironment();
  });
});

document.querySelectorAll('#sourceMode .seg').forEach((segment) => {
  segment.addEventListener('click', () => {
    document.querySelectorAll('#sourceMode .seg').forEach((other) => other.classList.toggle('is-active', other === segment));
    document.querySelectorAll('[data-source]').forEach((block) => {
      block.hidden = block.dataset.source !== segment.dataset.mode;
    });
  });
});

/* ------------------------------------------------------------------- pickers */

function wirePicker(inputId, chipId, key, previewId) {
  const input = $(inputId);
  if (!input) return;
  input.addEventListener('change', async () => {
    const file = input.files && input.files[0];
    if (!file) return;
    const chip = $(chipId);
    if (chip) { chip.textContent = 'uploading…'; chip.hidden = false; }
    try {
      state.uploads[key] = await upload(file);
      if (chip) chip.textContent = file.name;
      if (previewId) {
        const preview = $(previewId);
        preview.src = fileUrl(state.uploads[key]);
        preview.hidden = false;
      }
      if (key === 'voiceSample') $('voiceChecks').hidden = false;
    } catch (error) {
      if (chip) chip.hidden = true;
      toast(error.message);
    }
  });
}

wirePicker('presenterImage', 'presenterImageName', 'presenterImage', 'presenterPreview');
wirePicker('voiceSample', 'voiceSampleName', 'voiceSample');
wirePicker('scriptFile', 'scriptFileName', 'scriptFile');
wirePicker('music', 'musicName', 'music');

$('supportingMedia').addEventListener('change', async () => {
  const files = Array.from($('supportingMedia').files || []);
  for (const file of files) {
    try {
      state.uploads.supporting.push({ path: await upload(file), name: file.name });
    } catch (error) {
      toast(error.message);
    }
  }
  renderSupporting();
});

function renderSupporting() {
  $('supportingList').innerHTML = state.uploads.supporting
    .map((item, index) => `<li>${escapeHtml(item.name)} <button class="ghost small" data-remove="${index}">×</button></li>`)
    .join('');
  $('supportingList').querySelectorAll('[data-remove]').forEach((button) => {
    button.addEventListener('click', () => {
      state.uploads.supporting.splice(Number(button.dataset.remove), 1);
      renderSupporting();
    });
  });
}

/* ------------------------------------------------------------------- create */

function collectJob() {
  const mode = document.querySelector('#sourceMode .seg.is-active').dataset.mode;
  return {
    topic: mode === 'topic' ? $('topic').value.trim() : '',
    scriptText: mode === 'script' ? $('scriptText').value.trim() : '',
    scriptFile: mode === 'script' ? state.uploads.scriptFile : '',
    presenterImage: state.uploads.presenterImage,
    voiceSample: state.uploads.voiceSample,
    music: state.uploads.music,
    supportingMedia: state.uploads.supporting.map((item) => item.path),
    language: $('language').value.trim() || 'auto',
    duration: Number($('duration').value) || 60,
    aspect: $('aspect').value,
    fps: Number($('fps').value) || 30,
    style: $('style').value.trim(),
    watermark: $('watermark').value.trim(),
    cta: $('cta').value.trim(),
    accentColor: $('accentColor').value,
    captions: $('captions').checked,
    callouts: $('callouts').checked,
    voiceId: $('voiceId').value,
    rate: Number($('rate').value) || 1,
    imageViewed: $('imageViewed').checked,
    singleClearFace: $('singleClearFace').checked,
    noUnwantedText: $('noUnwantedText').checked,
    rightsConfirmed: $('rightsConfirmed').checked,
    adultConfirmed: $('adultConfirmed').checked,
    voiceListened: $('voiceListened').checked,
    singleSpeaker: $('singleSpeaker').checked,
    voiceCloneApproved: $('voiceCloneApproved').checked,
  };
}

function validate(job) {
  if (!job.presenterImage) return 'Choose a presenter image first.';
  if (!job.topic && !job.scriptText && !job.scriptFile) return 'Give a topic, or paste or choose a script.';
  if (!job.imageViewed || !job.singleClearFace || !job.noUnwantedText) {
    return 'Preflight blocks the render until the three manual image checks are ticked.';
  }
  if (job.voiceSample && (!job.voiceListened || !job.singleSpeaker)) {
    return 'Tick the voice sample review checks, or remove the sample.';
  }
  return '';
}

async function createJob(thenRun) {
  const job = collectJob();
  const problem = validate(job);
  if (problem) { $('createHint').textContent = problem; toast(problem, 5000); return; }

  $('createHint').textContent = 'Creating the job…';
  $('createRun').disabled = true;
  $('createOnly').disabled = true;

  try {
    const created = await api('/api/jobs', { method: 'POST', body: JSON.stringify(job) });
    state.selectedJob = created.directory;
    $('createHint').textContent = `Created in ${created.directory}`;
    await loadJobs();
    if (thenRun) await startRun(created.directory);
  } catch (error) {
    $('createHint').textContent = error.message;
    toast(error.message, 6000);
  } finally {
    $('createRun').disabled = false;
    $('createOnly').disabled = false;
  }
}

$('createRun').addEventListener('click', () => createJob(true));
$('createOnly').addEventListener('click', () => createJob(false));

/* ---------------------------------------------------------------------- runs */

async function startRun(directory) {
  const started = await api('/api/jobs/run', { method: 'POST', body: JSON.stringify({ directory }) });
  openRunbar(started.runId, directory);
}

function openRunbar(runId, directory) {
  $('runbar').hidden = false;
  $('runLog').textContent = '';
  $('runResult').innerHTML = '';
  $('runFill').style.width = '0%';
  $('runStage').textContent = 'Starting';
  $('runMessage').textContent = '';
  $('cancelRun').disabled = false;
  $('cancelRun').dataset.runId = runId;

  if (state.runSource) state.runSource.close();
  const source = new EventSource(`/api/runs/${runId}/events`);
  state.runSource = source;

  source.onmessage = (event) => {
    const payload = JSON.parse(event.data);
    if (payload.type === 'progress') {
      $('runStage').textContent = payload.stage.replace(/_/g, ' ');
      $('runMessage').textContent = payload.message;
      $('runFill').style.width = `${Math.round(payload.progress * 100)}%`;
      const log = $('runLog');
      log.textContent += `${payload.message}\n`;
      log.scrollTop = log.scrollHeight;
      return;
    }
    if (payload.type === 'done') {
      source.close();
      state.runSource = null;
      $('cancelRun').disabled = true;
      renderRunResult(payload, directory);
      loadJobs();
    }
  };

  source.onerror = () => {
    source.close();
    state.runSource = null;
  };
}

function renderRunResult(payload, directory) {
  const warnings = (payload.warnings || [])
    .map((warning) => `<li>${escapeHtml(warning)}</li>`).join('');

  if (payload.outcome === 'NeedsApproval') {
    $('runStage').textContent = 'Waiting on you';
    $('runFill').style.width = '100%';
    $('runResult').innerHTML = `
      <div class="callout warn">
        <strong>${escapeHtml(payload.message)}</strong>
        <pre>${escapeHtml(payload.approvalRequest)}</pre>
        ${payload.pilot ? `<video controls preload="metadata" src="${fileUrl(payload.pilot)}"></video>` : ''}
        <div class="artifacts">
          <button class="primary small" id="approveButton">Approve and continue</button>
        </div>
      </div>`;
    $('approveButton').addEventListener('click', async () => {
      $('approveButton').disabled = true;
      await api('/api/jobs/approve', {
        method: 'POST',
        body: JSON.stringify({ directory, kind: payload.approvalKind }),
      });
      await startRun(directory);
    });
    return;
  }

  if (payload.outcome !== 'Completed') {
    $('runStage').textContent = payload.outcome === 'Cancelled' ? 'Cancelled' : 'Failed';
    $('runResult').innerHTML = `<div class="callout bad"><strong>${escapeHtml(payload.message)}</strong></div>`;
    return;
  }

  $('runStage').textContent = 'Done';
  $('runFill').style.width = '100%';
  $('runResult').innerHTML = `
    <div class="callout ${payload.qaPassed ? 'good' : 'warn'}">
      <strong>${escapeHtml(payload.message)}</strong>
      ${payload.master ? `<video controls preload="metadata" src="${fileUrl(payload.master)}"></video>` : ''}
      <div class="artifacts">
        ${payload.master ? `<a class="chip" href="${fileUrl(payload.master, true)}">Download master</a>` : ''}
        ${payload.share ? `<a class="chip" href="${fileUrl(payload.share, true)}">Download share copy</a>` : ''}
        ${payload.captions ? `<a class="chip" href="${fileUrl(payload.captions, true)}">Captions .srt</a>` : ''}
        ${payload.contactSheet ? `<a class="chip" href="${fileUrl(payload.contactSheet)}" target="_blank" rel="noopener">Contact sheet</a>` : ''}
        <button class="chip" id="revealOutputs">Open folder</button>
      </div>
      ${warnings ? `<ul class="notes">${warnings}</ul>` : ''}
    </div>`;

  const reveal = $('revealOutputs');
  if (reveal) {
    reveal.addEventListener('click', () =>
      api('/api/reveal', { method: 'POST', body: JSON.stringify({ path: payload.master || directory }) })
        .catch((error) => toast(error.message)));
  }
}

$('cancelRun').addEventListener('click', () => {
  const runId = $('cancelRun').dataset.runId;
  if (runId) api('/api/jobs/cancel', { method: 'POST', body: JSON.stringify({ runId }) }).catch(() => {});
});

$('closeRun').addEventListener('click', () => {
  if (state.runSource) { state.runSource.close(); state.runSource = null; }
  $('runbar').hidden = true;
});

/* ---------------------------------------------------------------------- jobs */

async function loadJobs() {
  try {
    const data = await api('/api/jobs');
    $('workspacePath').textContent = data.workspace;
    $('jobList').innerHTML = data.jobs.map((job) => `
      <li><button data-dir="${escapeHtml(job.directory)}" class="${job.directory === state.selectedJob ? 'is-active' : ''}">
        <span class="name">${escapeHtml(job.title || job.jobId)}</span>
        <span class="meta"><span class="state ${escapeHtml(job.state)}">${escapeHtml(job.state.replace(/_/g, ' '))}</span> ${escapeHtml((job.updatedUtc || '').slice(0, 16).replace('T', ' '))}</span>
      </button></li>`).join('') || '<li class="empty">No jobs yet.</li>';

    $('jobList').querySelectorAll('[data-dir]').forEach((button) => {
      button.addEventListener('click', () => showJob(button.dataset.dir));
    });
  } catch (error) {
    toast(error.message);
  }
}

$('refreshJobs').addEventListener('click', loadJobs);

async function showJob(directory) {
  state.selectedJob = directory;
  await loadJobs();
  try {
    const data = await api(`/api/job?dir=${encodeURIComponent(directory)}`);
    const job = data.job;
    const resolve = (relative) => `${directory}/${relative}`;
    const outputs = data.outputs || [];
    const master = outputs.find((name) => name.endsWith('-master.mp4'));
    const sheet = outputs.find((name) => name.endsWith('-contact-sheet.png'));

    $('jobDetail').innerHTML = `
      <div class="card-head">
        <h2>${escapeHtml(job.job_id)}</h2>
        <div>
          <button class="primary small" id="runJob">${job.state === 'verified' ? 'Re-render' : 'Run'}</button>
          <button class="ghost small" id="revealJob">Open folder</button>
        </div>
      </div>
      <dl class="kv">
        <dt>State</dt><dd><span class="state ${escapeHtml(job.state)}">${escapeHtml(job.state.replace(/_/g, ' '))}</span></dd>
        <dt>Source</dt><dd>${escapeHtml(job.input.topic || job.input.script_path || '—')}</dd>
        <dt>Format</dt><dd>${job.creative.width}×${job.creative.height} · ${job.creative.fps}fps · ${escapeHtml(job.creative.aspect)}</dd>
        <dt>Voice</dt><dd>${escapeHtml(job.voice.provider || '—')}${job.voice.voice_id ? ` · ${escapeHtml(job.voice.voice_id)}` : ''}</dd>
        <dt>Presenter track</dt><dd>${escapeHtml(job.capabilities.main_presenter.provider || '—')}${job.capabilities.main_presenter.notes ? ` — ${escapeHtml(job.capabilities.main_presenter.notes)}` : ''}</dd>
        <dt>Word timings</dt><dd>${escapeHtml(job.capabilities.word_timestamp_asr.provider || '—')}</dd>
        <dt>Directory</dt><dd>${escapeHtml(directory)}</dd>
      </dl>
      ${master ? `<video controls preload="metadata" src="${fileUrl(resolve('outputs/' + master))}"></video>` : ''}
      ${sheet ? `<img class="sheet" alt="Contact sheet" src="${fileUrl(resolve('outputs/' + sheet))}">` : ''}
      <div class="artifacts">
        ${outputs.map((name) => `<a class="chip" href="${fileUrl(resolve('outputs/' + name), true)}">${escapeHtml(name)}</a>`).join('')}
      </div>
      ${job.artifacts.script ? `<div class="artifacts">
        <a class="chip" href="/api/text?path=${encodeURIComponent(resolve(job.artifacts.script))}" target="_blank" rel="noopener">Script</a>
        ${job.artifacts.beat_sheet ? `<a class="chip" href="/api/text?path=${encodeURIComponent(resolve(job.artifacts.beat_sheet))}" target="_blank" rel="noopener">Beat sheet</a>` : ''}
        ${job.artifacts.timeline ? `<a class="chip" href="/api/text?path=${encodeURIComponent(resolve(job.artifacts.timeline))}" target="_blank" rel="noopener">Timeline</a>` : ''}
        ${job.qa.manual_visual_review ? `<a class="chip" href="/api/text?path=${encodeURIComponent(resolve(job.qa.manual_visual_review))}" target="_blank" rel="noopener">QA report</a>` : ''}
        <a class="chip" href="/api/text?path=${encodeURIComponent(resolve('qa/reports/run-log.txt'))}" target="_blank" rel="noopener">Run log</a>
      </div>` : ''}`;

    $('runJob').addEventListener('click', async () => {
      $('runJob').disabled = true;
      try { await startRun(directory); } catch (error) { toast(error.message); }
      $('runJob').disabled = false;
    });
    $('revealJob').addEventListener('click', () =>
      api('/api/reveal', { method: 'POST', body: JSON.stringify({ path: directory }) }).catch((error) => toast(error.message)));
  } catch (error) {
    $('jobDetail').innerHTML = `<div class="callout bad">${escapeHtml(error.message)}</div>`;
  }
}

/* ----------------------------------------------------------------- settings */

async function loadSettings() {
  const data = await api('/api/settings');
  state.settings = data.settings;
  $('version').textContent = `v${data.version} · ${data.workspace}`;

  $('aspect').innerHTML = data.aspects
    .map((aspect) => `<option ${aspect === data.settings.defaults.aspect ? 'selected' : ''}>${escapeHtml(aspect)}</option>`)
    .join('');
  $('fps').value = String(data.settings.defaults.fps);
  $('duration').value = String(data.settings.defaults.duration_target_s);
  $('language').value = data.settings.defaults.language;
  $('accentColor').value = data.settings.defaults.accent_color;
  $('style').value = data.settings.defaults.style;

  $('scriptProvider').value = data.settings.script.provider;
  $('scriptModel').value = data.settings.script.model;
  $('asrProvider').value = data.settings.asr.provider;
  $('whisperModelPath').value = data.settings.asr.whisper_model_path;
  $('presenterMode').value = data.settings.presenter.mode;
  $('workspace').value = data.settings.workspace;
  $('ffmpegPath').value = data.settings.ffmpeg_path;
  $('captionFont').value = data.settings.caption_font;

  const motion = data.settings.presenter.motion;
  $('motionZoom').value = motion.zoom_percent;
  $('motionSway').value = motion.sway_pixels;
  $('motionBackground').value = motion.background;
  $('motionGrade').checked = motion.grade;
  $('motionVignette').checked = motion.vignette;

  const remote = data.settings.presenter.remote;
  $('remoteEnabled').checked = remote.enabled;
  $('remoteProvider').value = remote.provider;
  $('remoteModel').value = remote.model;
  $('remoteSubmit').value = remote.submit_url;
  $('remoteStatus').value = remote.status_url_template;
  $('remoteSecret').value = remote.secret_key || 'PRESENTER_API_KEY';
  $('remoteAuthHeader').value = remote.auth_header || 'Authorization';
  $('remoteAuthScheme').value = remote.auth_scheme || 'Bearer';
  $('remoteTaskPath').value = remote.task_id_path;
  $('remoteStatusPath').value = remote.status_path;
  $('remoteResultPath').value = remote.result_url_path;
  $('remotePrice').value = remote.price_per_second ?? '';
  $('remotePriceDate').value = remote.price_evidence_date || '';
  $('remoteTemplate').value = remote.request_template || JSON.stringify({
    model: '{{MODEL}}',
    image: '{{IMAGE_DATA_URI}}',
    audio: '{{AUDIO_DATA_URI}}',
    prompt: '{{PROMPT}}',
    negative_prompt: '{{NEGATIVE_PROMPT}}',
    width: '{{WIDTH}}',
    height: '{{HEIGHT}}',
    fps: '{{FPS}}',
  }, null, 2);

  $('secretList').innerHTML = data.knownSecrets.map((secret) => `
    <div class="secret-row">
      <div class="field">
        <label for="secret-${escapeHtml(secret.key)}">${escapeHtml(secret.displayName)} — ${escapeHtml(secret.purpose)}</label>
        <input id="secret-${escapeHtml(secret.key)}" type="password" autocomplete="off"
               placeholder="${secret.configured ? '•••••••• stored' : 'not set'}">
      </div>
      <button class="ghost small" data-secret="${escapeHtml(secret.key)}">Save</button>
    </div>`).join('');

  $('secretList').querySelectorAll('[data-secret]').forEach((button) => {
    button.addEventListener('click', async () => {
      const key = button.dataset.secret;
      const input = $(`secret-${key}`);
      try {
        const result = await api('/api/secrets', { method: 'POST', body: JSON.stringify({ key, value: input.value }) });
        input.value = '';
        input.placeholder = result.configured ? '•••••••• stored' : 'not set';
        toast(result.configured ? `${key} stored.` : `${key} removed.`);
        loadVoices();
      } catch (error) {
        toast(error.message);
      }
    });
  });

  $('writerHint').textContent = data.knownSecrets.some((secret) => secret.configured && /ANTHROPIC|OPENAI/.test(secret.key))
    ? 'A model is configured, so the script will be fully drafted from your topic.'
    : 'No model key is set, so the built-in outline writer will build the structure and you edit the narration. Add a key in Settings for a fully drafted script.';
}

$('saveSettings').addEventListener('click', async () => {
  const settings = state.settings;
  settings.workspace = $('workspace').value.trim();
  settings.ffmpeg_path = $('ffmpegPath').value.trim();
  settings.caption_font = $('captionFont').value.trim();
  settings.script.provider = $('scriptProvider').value;
  settings.script.model = $('scriptModel').value.trim();
  settings.asr.provider = $('asrProvider').value;
  settings.asr.whisper_model_path = $('whisperModelPath').value.trim();
  settings.presenter.mode = $('presenterMode').value;
  settings.presenter.motion.zoom_percent = Number($('motionZoom').value);
  settings.presenter.motion.sway_pixels = Number($('motionSway').value);
  settings.presenter.motion.background = $('motionBackground').value;
  settings.presenter.motion.grade = $('motionGrade').checked;
  settings.presenter.motion.vignette = $('motionVignette').checked;

  const remote = settings.presenter.remote;
  remote.enabled = $('remoteEnabled').checked;
  remote.provider = $('remoteProvider').value.trim();
  remote.model = $('remoteModel').value.trim();
  remote.submit_url = $('remoteSubmit').value.trim();
  remote.status_url_template = $('remoteStatus').value.trim();
  remote.secret_key = $('remoteSecret').value.trim();
  remote.auth_header = $('remoteAuthHeader').value.trim();
  remote.auth_scheme = $('remoteAuthScheme').value.trim();
  remote.task_id_path = $('remoteTaskPath').value.trim();
  remote.status_path = $('remoteStatusPath').value.trim();
  remote.result_url_path = $('remoteResultPath').value.trim();
  remote.request_template = $('remoteTemplate').value.trim();
  remote.price_per_second = $('remotePrice').value === '' ? null : Number($('remotePrice').value);
  remote.price_evidence_date = $('remotePriceDate').value;

  settings.voice.provider = $('voiceProvider').value || 'auto';
  settings.voice.voice_id = $('voiceId').value;

  try {
    await api('/api/settings', { method: 'POST', body: JSON.stringify(settings) });
    $('settingsHint').textContent = 'Saved.';
    toast('Settings saved.');
    await loadSettings();
    await loadEnvironment();
  } catch (error) {
    $('settingsHint').textContent = error.message;
  }
});

/* ------------------------------------------------------------------- voices */

async function loadVoices() {
  try {
    const data = await api('/api/voices');
    state.voiceGroups = data.groups;
    $('voiceProvider').innerHTML = ['<option value="auto">Auto — best available</option>']
      .concat(data.groups.map((group) =>
        `<option value="${escapeHtml(group.provider)}">${escapeHtml(group.provider)}${group.remote ? ' (remote)' : ''}</option>`))
      .join('');
    if (state.settings && state.settings.voice.provider) {
      $('voiceProvider').value = state.settings.voice.provider;
    }
    renderVoiceOptions();
  } catch (error) {
    $('voiceProvider').innerHTML = '<option value="auto">Auto</option>';
  }
}

function renderVoiceOptions() {
  const provider = $('voiceProvider').value;
  const group = state.voiceGroups.find((candidate) => candidate.provider === provider);
  const voices = group ? group.voices : state.voiceGroups.flatMap((candidate) => candidate.voices);
  $('voiceId').innerHTML = ['<option value="">Engine default</option>']
    .concat(voices.slice(0, 400).map((voice) =>
      `<option value="${escapeHtml(voice.id)}">${escapeHtml(voice.displayName)}${voice.language ? ` — ${escapeHtml(voice.language)}` : ''}</option>`))
    .join('');
  if (state.settings && state.settings.voice.voice_id) {
    $('voiceId').value = state.settings.voice.voice_id;
  }
}

$('voiceProvider').addEventListener('change', renderVoiceOptions);

/* -------------------------------------------------------------- environment */

async function loadEnvironment() {
  const body = $('envBody');
  body.innerHTML = '<p class="empty">Checking…</p>';
  try {
    const report = await api('/api/environment');
    const line = (label, ok, detail) => `
      <div class="status-line"><span class="dot ${ok ? 'ok' : 'no'}"></span>${escapeHtml(label)}
      <span class="detail">${escapeHtml(detail || '')}</span></div>`;

    body.innerHTML = `
      ${report.problems.length ? `<div class="callout bad"><strong>Not ready yet</strong><ul class="notes">${
        report.problems.map((problem) => `<li>${escapeHtml(problem)}</li>`).join('')}</ul></div>` : ''}
      ${line('FFmpeg', Boolean(report.ffmpeg_path), report.ffmpeg_path || 'not found')}
      ${line('Version', Boolean(report.ffmpeg_version), report.ffmpeg_version)}
      ${line('H.264 encoder', report.libx264, 'libx264')}
      ${line('AAC encoder', report.aac, 'aac')}
      ${line('Loudness normalization', report.loudnorm, 'loudnorm')}
      ${line('Motion plate', report.zoompan, 'zoompan')}
      ${line('Burned-in captions', report.subtitle_burn_in, report.subtitle_burn_in ? 'libass' : 'sidecar .srt only')}
      ${line('Speech engines', report.offline_voices.length > 0, report.offline_voices.join(', ') || 'none')}
      ${line('Configured providers', true, report.configured_providers.join(', ') || 'none — running fully local')}
      ${line('Workspace', true, report.workspace)}
      ${line('Platform', true, report.platform)}
      ${report.notes.length ? `<ul class="notes">${report.notes.map((note) => `<li>${escapeHtml(note)}</li>`).join('')}</ul>` : ''}`;
  } catch (error) {
    body.innerHTML = `<div class="callout bad">${escapeHtml(error.message)}</div>`;
  }
}

$('refreshEnv').addEventListener('click', loadEnvironment);

$('installFfmpeg').addEventListener('click', async () => {
  const button = $('installFfmpeg');
  button.disabled = true;
  button.textContent = 'Downloading…';
  try {
    const result = await api('/api/environment/install-ffmpeg', { method: 'POST' });
    toast(result.ok ? `FFmpeg installed to ${result.path}` : result.error, 6000);
  } catch (error) {
    toast(error.message, 7000);
  } finally {
    button.disabled = false;
    button.textContent = 'Install FFmpeg';
    loadEnvironment();
  }
});

/* --------------------------------------------------------------------- boot */

(async function boot() {
  try {
    await loadSettings();
    await loadVoices();
    await loadJobs();
    await loadEnvironment();
  } catch (error) {
    toast(error.message, 8000);
  }
})();
