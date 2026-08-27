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
    reviewScript: $('reviewScript').checked,
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

async function createJob(thenRun, preview) {
  const job = collectJob();
  const problem = validate(job);
  if (problem) { $('createHint').textContent = problem; toast(problem, 5000); return; }

  $('createHint').textContent = 'Creating the job…';
  $('createRun').disabled = true;
  $('createPreview').disabled = true;
  $('createOnly').disabled = true;

  try {
    const created = await api('/api/jobs', { method: 'POST', body: JSON.stringify(job) });
    state.selectedJob = created.directory;
    $('createHint').textContent = `Created in ${created.directory}`;
    await loadJobs();
    if (thenRun) await startRun(created.directory, { preview });
  } catch (error) {
    $('createHint').textContent = error.message;
    toast(error.message, 6000);
  } finally {
    $('createRun').disabled = false;
    $('createPreview').disabled = false;
    $('createOnly').disabled = false;
  }
}

$('createRun').addEventListener('click', () => createJob(true, false));
$('createPreview').addEventListener('click', () => createJob(true, true));
$('createOnly').addEventListener('click', () => createJob(false, false));

/* ---------------------------------------------------------------------- runs */

async function startRun(directory, options = {}) {
  const started = await api('/api/jobs/run', {
    method: 'POST',
    body: JSON.stringify({ directory, preview: Boolean(options.preview) }),
  });
  openRunbar(started.runId, directory);
}

function openRunbar(runId, directory) {
  $('runbar').hidden = false;
  $('scriptEditor').hidden = true;
  $('scriptEditor').innerHTML = '';
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
          <button class="primary small" id="approveButton">${
            payload.approvalKind === 'script' ? 'Approve the script and continue' : 'Approve and continue'}</button>
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

    if (payload.approvalKind === 'script') openScriptEditor(directory);
    return;
  }

  if (payload.outcome !== 'Completed') {
    $('runStage').textContent = payload.outcome === 'Cancelled' ? 'Cancelled' : 'Failed';
    $('runResult').innerHTML = `<div class="callout bad"><strong>${escapeHtml(payload.message)}</strong></div>`;
    return;
  }

  if (payload.preview) {
    $('runStage').textContent = 'Preview';
    $('runFill').style.width = '100%';
    $('runResult').innerHTML = `
      <div class="callout good">
        <strong>${escapeHtml(payload.message)}</strong>
        <video controls preload="metadata" src="${fileUrl(payload.preview)}"></video>
        <div class="artifacts">
          <button class="primary small" id="renderFull">Render the delivery master</button>
          <button class="chip" id="editScriptFromPreview">Edit the script</button>
        </div>
        ${warnings ? `<ul class="notes">${warnings}</ul>` : ''}
      </div>`;
    $('renderFull').addEventListener('click', async () => {
      $('renderFull').disabled = true;
      await startRun(directory);
    });
    $('editScriptFromPreview').addEventListener('click', () => openScriptEditor(directory));
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

/* ------------------------------------------------------------- script editor */

const ROLES = ['hook', 'beat', 'synthesis', 'close'];

async function openScriptEditor(directory) {
  const panel = $('scriptEditor');
  panel.hidden = false;
  panel.innerHTML = '<p class="hint">Loading the script…</p>';

  let data;
  try {
    data = await api(`/api/job/script?dir=${encodeURIComponent(directory)}`);
  } catch (error) {
    panel.innerHTML = `<div class="callout bad">${escapeHtml(error.message)}</div>`;
    return;
  }

  const render = (beats) => {
    panel.innerHTML = `
      <div class="script-head">
        <strong>Script</strong>
        <span class="hint" id="scriptEstimate"></span>
      </div>
      <div id="beatList">${beats.map((beat, index) => beatRow(beat, index)).join('')}</div>
      <div class="artifacts">
        <button class="ghost small" id="addBeat">Add a beat</button>
        <button class="primary small" id="saveScript">Save and approve</button>
        <button class="ghost small" id="saveScriptOnly">Save only</button>
      </div>
      <p class="hint" id="scriptHint"></p>`;

    updateEstimate();

    panel.querySelectorAll('textarea[data-narration]').forEach((area) => {
      area.addEventListener('input', updateEstimate);
    });

    panel.querySelectorAll('[data-remove-beat]').forEach((button) => {
      button.addEventListener('click', () => {
        const current = collectBeats();
        current.splice(Number(button.dataset.removeBeat), 1);
        render(current.length > 0 ? current : [{ role: 'hook', title: '', narration: '', keyword: '' }]);
      });
    });

    $('addBeat').addEventListener('click', () => {
      const current = collectBeats();
      current.splice(Math.max(0, current.length - 1), 0, { role: 'beat', title: '', narration: '', keyword: '' });
      render(current);
    });

    $('saveScript').addEventListener('click', () => saveScript(true));
    $('saveScriptOnly').addEventListener('click', () => saveScript(false));
  };

  const beatRow = (beat, index) => `
    <div class="beat" data-beat="${index}">
      <div class="row">
        <div class="field">
          <label>Role</label>
          <select data-role>${ROLES.map((role) =>
            `<option ${role === beat.role ? 'selected' : ''}>${role}</option>`).join('')}</select>
        </div>
        <div class="field grow">
          <label>Title</label>
          <input type="text" data-title value="${escapeHtml(beat.title || '')}">
        </div>
        <div class="field grow">
          <label>Callout keyword</label>
          <input type="text" data-keyword value="${escapeHtml(beat.keyword || '')}">
        </div>
        <button class="ghost small" data-remove-beat="${index}" title="Remove this beat">×</button>
      </div>
      <label>Spoken narration</label>
      <textarea rows="3" data-narration>${escapeHtml(beat.narration || '')}</textarea>
    </div>`;

  const collectBeats = () => Array.from(panel.querySelectorAll('.beat')).map((row) => ({
    role: row.querySelector('[data-role]').value,
    title: row.querySelector('[data-title]').value,
    keyword: row.querySelector('[data-keyword]').value,
    narration: row.querySelector('[data-narration]').value,
  }));

  // Mirrors the server's estimate closely enough to warn before a run starts.
  const updateEstimate = () => {
    const text = collectBeats().map((beat) => beat.narration).join(' ');
    const cjk = /[\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uac00-\ud7af]/.test(text);
    const seconds = cjk
      ? text.replace(/\s/g, '').length / 5.2
      : text.split(/\s+/).filter(Boolean).length / 2.6;
    const target = data.targetSeconds || 0;
    const drift = target > 0 ? Math.round(((seconds - target) / target) * 100) : 0;
    $('scriptEstimate').textContent = target > 0
      ? `about ${seconds.toFixed(0)}s spoken · target ${target}s (${drift >= 0 ? '+' : ''}${drift}%)`
      : `about ${seconds.toFixed(0)}s spoken`;
  };

  const saveScript = async (approve) => {
    const hint = $('scriptHint');
    hint.textContent = 'Saving…';
    try {
      const result = await api('/api/job/script', {
        method: 'POST',
        body: JSON.stringify({ directory, beats: collectBeats(), approve }),
      });
      hint.textContent = result.audioInvalidated
        ? 'Saved. The wording changed, so the locked narration was discarded and will be spoken again.'
        : `Saved — ${result.beats} beats, about ${result.estimatedSeconds}s.`;
      if (approve) {
        panel.hidden = true;
        await startRun(directory);
      }
    } catch (error) {
      hint.textContent = error.message;
    }
  };

  render(data.script.beats.map((beat) => ({
    role: beat.role,
    title: beat.title,
    keyword: beat.keyword,
    narration: beat.narration,
  })));
}

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
  if (state.selectedJob !== directory) {
    $('timelineEditor').hidden = true;
    $('timelineEditor').innerHTML = '';
  }

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
          <button class="ghost small" id="previewJob">Fast preview</button>
          <button class="ghost small" id="editScript">Edit script</button>
          <button class="ghost small" id="editTimeline">Timeline</button>
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
    $('previewJob').addEventListener('click', async () => {
      $('previewJob').disabled = true;
      try { await startRun(directory, { preview: true }); } catch (error) { toast(error.message); }
      $('previewJob').disabled = false;
    });
    $('editScript').addEventListener('click', () => {
      $('runbar').hidden = false;
      $('runResult').innerHTML = '';
      $('runLog').textContent = '';
      $('runStage').textContent = 'Script';
      $('runMessage').textContent = job.job_id;
      openScriptEditor(directory);
    });
    $('editTimeline').addEventListener('click', () => openTimelineEditor(directory));
    retranslate();
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
  $('renderEncoder').value = data.settings.render.encoder;
  $('previewHeight').value = data.settings.render.preview_height;
  $('reviewBeforeAudio').checked = data.settings.script.review_before_audio;
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

  // This line is written after the settings load, so it has missed the initial pass.
  retranslate();
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
  settings.render.encoder = $('renderEncoder').value;
  settings.render.preview_height = Number($('previewHeight').value) || 640;
  settings.script.review_before_audio = $('reviewBeforeAudio').checked;
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
      ${line('Video encoder', true, report.video_encoder)}
      ${line('Hardware encoders', report.hardware_encoders.length > 0, report.hardware_encoders.join(', ') || 'none detected')}
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

/* ----------------------------------------------------------------- timeline */

/*
 * The timeline editor. The narration is the clock, so the waveform is the ruler and everything
 * else is drawn against it: chapters, the presenter track, the inserts you can move, and the
 * punch-ins and shots the run derived. Only inserts are draggable — the rest is measured from
 * the audio, and letting someone drag a chapter boundary would just be lying to them about what
 * the render will do.
 */
const timelineState = {
  directory: null,
  data: null,
  assignments: new Map(),
  drag: null,
  dirty: false,
};

const LANES = [
  { key: 'chapters', label: 'Chapters', height: 34 },
  { key: 'presenter', label: 'Presenter', height: 26 },
  { key: 'inserts', label: 'B-roll', height: 34 },
  { key: 'shots', label: 'Shots', height: 22 },
  { key: 'punches', label: 'Emphasis', height: 18 },
];

const GUTTER = 96;
const WAVE_HEIGHT = 72;
const LANE_GAP = 6;

async function openTimelineEditor(directory) {
  const panel = $('timelineEditor');
  panel.hidden = false;
  panel.innerHTML = '<p class="hint">Reading the timeline…</p>';

  let data;
  try {
    data = await api(`/api/job/timeline?dir=${encodeURIComponent(directory)}`);
  } catch (error) {
    panel.innerHTML = `<div class="callout bad">${escapeHtml(error.message)}</div>`;
    return;
  }

  if (!data.durationSeconds) {
    panel.innerHTML = `
      <div class="card-head"><h2>Timeline</h2></div>
      <p class="empty">This job has not been rendered yet. Chapters are measured from the spoken
      narration, so there is nothing to place media against until it has run once.</p>`;
    return;
  }

  timelineState.directory = directory;
  timelineState.data = data;
  timelineState.dirty = false;
  timelineState.assignments = new Map();
  data.chapters.forEach((chapter) => {
    if (chapter.isAssigned) {
      timelineState.assignments.set(chapter.index, {
        media: chapter.assigned || '',
        offsetSeconds: chapter.assignedOffset,
        durationSeconds: chapter.assignedDuration,
      });
    }
  });

  panel.innerHTML = `
    <div class="card-head">
      <h2>Timeline</h2>
      <div>
        <button class="primary small" id="timelineSave" disabled>Save</button>
        <button class="ghost small" id="timelineReset">Reset</button>
      </div>
    </div>
    <p class="hint" id="timelineHint">Drag a B-roll block to move it, or its right edge to change how long it holds.</p>
    <canvas id="timelineCanvas" class="timeline"></canvas>
    <div id="timelineRows" class="timeline-rows"></div>`;

  $('timelineSave').addEventListener('click', saveTimeline);
  $('timelineReset').addEventListener('click', () => openTimelineEditor(directory));

  const canvas = $('timelineCanvas');
  canvas.addEventListener('pointerdown', onTimelinePointerDown);
  canvas.addEventListener('pointermove', onTimelinePointerMove);
  canvas.addEventListener('pointerup', onTimelinePointerUp);
  canvas.addEventListener('pointercancel', onTimelinePointerUp);
  window.addEventListener('resize', drawTimeline);

  drawTimeline();
  renderTimelineRows();
  retranslate();
}

/** Where each lane sits vertically, so hit-testing and drawing agree by construction. */
function laneGeometry() {
  const rows = {};
  let y = WAVE_HEIGHT + LANE_GAP * 2;
  LANES.forEach((lane) => {
    rows[lane.key] = { top: y, height: lane.height, label: lane.label };
    y += lane.height + LANE_GAP;
  });
  return { rows, total: y };
}

function timelineScale() {
  const canvas = $('timelineCanvas');
  const width = canvas.clientWidth - GUTTER - 12;
  return {
    width,
    toX: (seconds) => GUTTER + (seconds / timelineState.data.durationSeconds) * width,
    toSeconds: (x) => ((x - GUTTER) / width) * timelineState.data.durationSeconds,
  };
}

/** The insert blocks as currently edited, which is what both drawing and hit-testing read. */
function currentInserts() {
  const data = timelineState.data;
  return data.chapters.map((chapter) => {
    const edited = timelineState.assignments.get(chapter.index);
    const fromRender = data.clips.find(
      (clip) => clip.kind === 'insert' && Math.abs(clip.startSeconds - chapter.startSeconds) < chapter.durationSeconds);

    if (edited) {
      if (!edited.media) return null;
      const offset = edited.offsetSeconds >= 0 ? edited.offsetSeconds : 0.3;
      const hold = edited.durationSeconds > 0
        ? edited.durationSeconds
        : Math.min(4.5, Math.max(0.6, chapter.durationSeconds - offset - 0.3));
      return { chapter, name: shortName(edited.media), start: chapter.startSeconds + offset, duration: hold, edited: true };
    }

    if (fromRender) {
      return { chapter, name: fromRender.name, start: fromRender.startSeconds, duration: fromRender.durationSeconds, edited: false };
    }

    return null;
  }).filter(Boolean);
}

function shortName(path) {
  return (path || '').split(/[\\/]/).pop();
}

function drawTimeline() {
  const canvas = $('timelineCanvas');
  if (!canvas || !timelineState.data) return;

  const { rows, total } = laneGeometry();
  const ratio = window.devicePixelRatio || 1;
  const cssWidth = canvas.clientWidth;
  canvas.style.height = `${total + 18}px`;
  canvas.width = Math.round(cssWidth * ratio);
  canvas.height = Math.round((total + 18) * ratio);

  const context = canvas.getContext('2d');
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.clearRect(0, 0, cssWidth, total + 18);

  // Colours come from the stylesheet so the canvas follows the light and dark themes.
  const style = getComputedStyle(document.documentElement);
  const token = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
  const ink = token('--text', '#e6edf3');
  const muted = token('--muted', '#98a5b3');
  const accent = token('--accent', '#f4c430');
  const accentInk = token('--accent-ink', '#1a1508');
  const line = token('--line', '#2a3441');
  const lane = {
    chapterA: token('--tl-chapter-a', '#2b3a4d'),
    chapterB: token('--tl-chapter-b', '#334559'),
    presenter: token('--tl-presenter', '#2f4a3c'),
    insert: token('--tl-insert', '#4a6fa5'),
    shot: token('--tl-shot', '#3d3552'),
  };

  const scale = timelineScale();
  const data = timelineState.data;

  context.font = '11px system-ui, sans-serif';
  context.textBaseline = 'middle';

  // Waveform: the spoken audio, drawn as a mirrored peak envelope.
  if (data.peaks && data.peaks.length) {
    context.fillStyle = line;
    context.fillRect(GUTTER, LANE_GAP, scale.width, WAVE_HEIGHT);
    context.fillStyle = muted;
    const mid = LANE_GAP + WAVE_HEIGHT / 2;
    const step = scale.width / data.peaks.length;
    data.peaks.forEach((peak, index) => {
      const height = Math.max(1, peak * (WAVE_HEIGHT / 2 - 2));
      context.fillRect(GUTTER + index * step, mid - height, Math.max(1, step - 0.4), height * 2);
    });
  }

  // Second ticks along the top of the waveform.
  context.fillStyle = muted;
  const tickStep = data.durationSeconds > 90 ? 15 : data.durationSeconds > 30 ? 5 : 2;
  for (let second = 0; second <= data.durationSeconds; second += tickStep) {
    const x = scale.toX(second);
    context.fillRect(x, LANE_GAP, 1, 5);
    context.fillText(`${second}s`, x + 3, LANE_GAP + 10);
  }

  LANES.forEach((lane) => {
    const row = rows[lane.key];
    context.fillStyle = muted;
    context.textAlign = 'right';
    context.fillText(row.label, GUTTER - 10, row.top + row.height / 2);
    context.textAlign = 'left';
    context.fillStyle = line;
    context.fillRect(GUTTER, row.top, scale.width, row.height);
  });

  const block = (row, start, duration, fill, text, textColour) => {
    const x = scale.toX(start);
    const width = Math.max(2, scale.toX(start + duration) - x);
    context.fillStyle = fill;
    context.fillRect(x, row.top, width, row.height);
    if (text && width > 26) {
      context.save();
      context.beginPath();
      context.rect(x + 3, row.top, width - 6, row.height);
      context.clip();
      context.fillStyle = textColour;
      context.fillText(text, x + 5, row.top + row.height / 2);
      context.restore();
    }
  };

  data.chapters.forEach((chapter, index) => {
    block(rows.chapters, chapter.startSeconds, chapter.durationSeconds,
      index % 2 ? lane.chapterA : lane.chapterB, `${chapter.index}· ${chapter.title}`, ink);
  });

  data.clips.filter((clip) => clip.kind === 'presenter').forEach((clip) => {
    block(rows.presenter, clip.startSeconds, clip.durationSeconds, lane.presenter, clip.label, ink);
  });

  currentInserts().forEach((insert) => {
    block(rows.inserts, insert.start, insert.duration,
      insert.edited ? accent : lane.insert, insert.name, insert.edited ? accentInk : ink);
    // A visible grab handle on the right edge, so resizing is discoverable.
    const right = scale.toX(insert.start + insert.duration);
    context.fillStyle = accentInk;
    context.fillRect(right - 3, rows.inserts.top + 4, 2, rows.inserts.height - 8);
  });

  data.shots.forEach((shot) => {
    block(rows.shots, shot.startSeconds, shot.durationSeconds, lane.shot, shot.label.split(' — ')[0], ink);
  });

  data.punchIns.forEach((punch) => {
    block(rows.punches, punch.startSeconds, punch.durationSeconds, accent, '', accentInk);
  });
}

function hitInsert(x, y) {
  const { rows } = laneGeometry();
  const row = rows.inserts;
  if (y < row.top || y > row.top + row.height) return null;

  const scale = timelineScale();
  for (const insert of currentInserts()) {
    const left = scale.toX(insert.start);
    const right = scale.toX(insert.start + insert.duration);
    if (x >= left && x <= right) {
      // The last few pixels resize; anything else moves.
      return { insert, mode: x > right - 8 ? 'resize' : 'move', grabSeconds: scale.toSeconds(x) - insert.start };
    }
  }

  return null;
}

function onTimelinePointerDown(event) {
  const canvas = $('timelineCanvas');
  const rect = canvas.getBoundingClientRect();
  const hit = hitInsert(event.clientX - rect.left, event.clientY - rect.top);
  if (!hit) return;

  timelineState.drag = hit;
  canvas.setPointerCapture(event.pointerId);
  canvas.style.cursor = hit.mode === 'resize' ? 'ew-resize' : 'grabbing';
}

function onTimelinePointerMove(event) {
  const canvas = $('timelineCanvas');
  const rect = canvas.getBoundingClientRect();
  const x = event.clientX - rect.left;
  const y = event.clientY - rect.top;

  if (!timelineState.drag) {
    const hover = hitInsert(x, y);
    canvas.style.cursor = hover ? (hover.mode === 'resize' ? 'ew-resize' : 'grab') : 'default';
    return;
  }

  const { insert, mode, grabSeconds } = timelineState.drag;
  const chapter = insert.chapter;
  const scale = timelineScale();
  const seconds = scale.toSeconds(x);

  // An insert belongs to its chapter: it cannot be dragged out of the passage it illustrates.
  const minOffset = 0;
  const maxOffset = Math.max(0, chapter.durationSeconds - 0.6);

  if (mode === 'move') {
    const offset = clamp(seconds - grabSeconds - chapter.startSeconds, minOffset, maxOffset);
    const hold = Math.min(insert.duration, chapter.durationSeconds - offset);
    setAssignment(chapter.index, insert, offset, hold);
  } else {
    const offset = insert.start - chapter.startSeconds;
    const hold = clamp(seconds - insert.start, 0.6, chapter.durationSeconds - offset);
    setAssignment(chapter.index, insert, offset, hold);
  }

  drawTimeline();
}

function onTimelinePointerUp(event) {
  const canvas = $('timelineCanvas');
  if (timelineState.drag) {
    canvas.releasePointerCapture?.(event.pointerId);
    timelineState.drag = null;
    canvas.style.cursor = 'default';
    renderTimelineRows();
  }
}

function clamp(value, low, high) {
  return Math.min(Math.max(value, low), Math.max(low, high));
}

function setAssignment(chapterIndex, insert, offsetSeconds, durationSeconds) {
  const existing = timelineState.assignments.get(chapterIndex);
  const media = existing?.media || fullPath(insert.name);
  timelineState.assignments.set(chapterIndex, {
    media,
    offsetSeconds: round(offsetSeconds),
    durationSeconds: round(durationSeconds),
  });
  markTimelineDirty();
}

/** Drag hands back a file name; the job stores the path it was given at init. */
function fullPath(name) {
  const match = (timelineState.data.supportingMedia || []).find((media) => media.name === name);
  return match ? match.path : name;
}

function round(value) {
  return Math.round(value * 100) / 100;
}

function markTimelineDirty() {
  timelineState.dirty = true;
  const save = $('timelineSave');
  if (save) save.disabled = false;
}

/** A row per chapter, so media can be assigned without dragging anything. */
function renderTimelineRows() {
  const host = $('timelineRows');
  if (!host) return;

  const media = timelineState.data.supportingMedia || [];
  host.innerHTML = timelineState.data.chapters.map((chapter) => {
    const edited = timelineState.assignments.get(chapter.index);
    const current = edited ? edited.media : '';
    const auto = !edited;
    const options = ['<option value="">— none —</option>']
      .concat(media.map((item) => `<option value="${escapeHtml(item.path)}"${item.path === current ? ' selected' : ''}>${escapeHtml(item.name)}</option>`))
      .join('');

    return `<label class="timeline-row">
      <span class="ix">${chapter.index}</span>
      <span class="title">${escapeHtml(chapter.title)}</span>
      <select data-chapter="${chapter.index}">${options}</select>
      <span class="hint">${auto ? 'automatic' : 'set by hand'}</span>
    </label>`;
  }).join('');

  host.querySelectorAll('select[data-chapter]').forEach((select) => {
    select.addEventListener('change', () => {
      const index = Number(select.dataset.chapter);
      timelineState.assignments.set(index, {
        media: select.value,
        offsetSeconds: -1,
        durationSeconds: 0,
      });
      markTimelineDirty();
      drawTimeline();
      renderTimelineRows();
    });
  });
}

async function saveTimeline() {
  const assignments = Array.from(timelineState.assignments.entries())
    .map(([chapterIndex, value]) => ({ chapterIndex, ...value }));

  try {
    await api('/api/job/timeline', {
      method: 'POST',
      body: JSON.stringify({ directory: timelineState.directory, assignments }),
    });
    timelineState.dirty = false;
    $('timelineSave').disabled = true;
    $('timelineHint').textContent = 'Saved. Run the job again to rebuild the timeline with these placements.';
  } catch (error) {
    toast(error.message);
  }
}

/* --------------------------------------------------------------------- i18n */

/*
 * The repository and the skill this app was ported from are Chinese-first, so the studio being
 * English-only was a gap rather than a preference.
 *
 * Translation is by exact source string rather than by a key attribute on every element. That is
 * the right trade for retrofitting: it needs no change to the markup, it reaches text the
 * JavaScript injects at runtime, and — the part that matters — anything not in the table is left
 * exactly as written. Provider names like Anthropic, NVENC and FFmpeg are absent on purpose and so
 * survive untouched, which is what you want from a translation table you did not hand-audit.
 */
const ZH = {
  // Shell and navigation
  'done by HelaO2 PVT LTD': '由 HelaO2 PVT LTD 制作',
  'Create': '新建',
  'Jobs': '任务',
  'Settings': '设置',
  'Environment': '运行环境',
  'Refresh': '刷新',
  'Re-check': '重新检测',
  'Cancel': '取消',
  'Close': '关闭',
  'Save settings': '保存设置',

  // Create tab
  'Write it from a topic': '根据主题生成',
  'I have a script': '我已有脚本',
  'Topic': '主题',
  'Script': '脚本',
  'Language': '语言',
  'Auto': '自动',
  'Target seconds': '目标时长（秒）',
  'Aspect': '画幅比例',
  'Frame rate': '帧率',
  'Choose an image': '选择图片',
  'Optional authorized voice sample': '可选：已授权的声音样本',
  'Optional music bed': '可选：背景音乐',
  'Add B-roll, screenshots, or charts': '添加空镜、截图或图表',
  'Create and render': '创建并渲染',
  'Create job only': '仅创建任务',
  'Fast preview': '快速预览',
  'Closing action': '结尾行动号召',
  'Watermark': '水印',
  'Accent': '强调色',
  'Caption font': '字幕字体',
  'Visual treatment': '视觉风格',
  'Manual review — required before any render': '人工检查 — 渲染前必须完成',
  'Voice sample review': '声音样本检查',
  'Presenter track': '主讲画面',
  'Motion plate': '动态画面',
  'Rendering': '渲染中',

  // Settings
  'Paths': '路径',
  'Workspace': '工作目录',
  'Credentials': '凭据',
  'Credential name': '凭据名称',
  'Name for the record': '记录用名称',
  'Provider': '服务商',
  'Model': '模型',
  'Engine': '引擎',
  'Rate': '语速',
  'Voice': '语音',
  'Script writer': '脚本生成',
  'Transcription': '语音转写',
  'Video encoder': '视频编码器',
  'Preview short edge': '预览短边像素',
  'Mode': '模式',
  'Background': '背景',
  'Default': '默认',
  'Scheme': '认证方式',
  'Auth header': '认证请求头',
  'Submit URL': '提交地址',
  'Status URL template': '状态查询地址模板',
  'Request body template': '请求体模板',
  'Task id path': '任务 ID 路径',
  'Status path': '状态字段路径',
  'Result URL path': '结果地址路径',
  'Known price per second': '已知每秒价格',
  'Price evidence date': '价格依据日期',
  'Aligner only': '仅使用对齐器',
  'Built-in outline writer': '内置大纲生成',
  'Local motion plate only': '仅本地动态画面',
  'Talking-head provider only': '仅使用数字人服务',
  'Auto — use a provider when configured': '自动 — 配置后使用服务商',
  'Auto — hardware when it works': '自动 — 硬件可用时启用',
  'Always fill the frame': '始终填满画面',
  'Always contain the image': '始终完整显示图片',

  // Environment
  'FFmpeg': 'FFmpeg',
  'Install FFmpeg': '安装 FFmpeg',

  // Timeline editor
  'Timeline': '时间线',
  'Chapters': '章节',
  'Presenter': '主讲',
  'B-roll': '空镜',
  'Shots': '镜头',
  'Emphasis': '强调',
  'Save': '保存',
  'Reset': '重置',
  'Edit script': '编辑脚本',
  'Open folder': '打开文件夹',
  'Run': '运行',
  'Re-render': '重新渲染',
  'automatic': '自动',
  'set by hand': '手动指定',
  'Drag a B-roll block to move it, or its right edge to change how long it holds.':
    '拖动空镜色块可移动位置，拖动右边缘可调整持续时间。',
  'Saved. Run the job again to rebuild the timeline with these placements.':
    '已保存。重新运行任务即可按此重建时间线。',
  'This job has not been rendered yet. Chapters are measured from the spoken\n      narration, so there is nothing to place media against until it has run once.':
    '该任务尚未渲染。章节来自实际语音的测量结果，需先运行一次才能安排素材。',

  // Section headings
  '1. What should the presenter say?': '1. 主讲要说什么？',
  '2. Presenter image': '2. 主讲图片',
  '3. Voice': '3. 语音',
  '4. Format': '4. 输出格式',
  '5. Supporting media': '5. 辅助素材',
  'OPTIONAL': '可选',

  // Manual review — the gate that matters most, so it is translated in full.
  'I looked at this image myself': '我已亲自查看这张图片',
  'It contains one clear adult face': '图中有一张清晰的成年人面孔',
  'No unwanted text, logos, or watermarks': '没有多余的文字、标识或水印',
  'I have the rights to use this image': '我拥有使用这张图片的权利',
  'The person shown is an adult': '图中人物为成年人',
  'I listened to this sample': '我已听过这段样本',
  'It has one clear speaker': '样本中只有一位清晰的说话人',
  'I am authorized to clone this voice': '我已获授权克隆该声音',

  // Options and toggles
  'Burn in captions': '烧录字幕',
  'Keyword callouts': '关键词标注',
  'Review the script before it is spoken': '朗读前先审阅脚本',
  'Always review the script before narration is synthesized': '合成旁白前始终审阅脚本',
  'Enable this provider': '启用该服务商',
  'Zoom over the take (%)': '画面推进幅度（%）',
  'Sway (px)': '晃动幅度（像素）',
  'Auto / blurred backdrop': '自动 / 模糊背景',
  'Light grade': '轻度调色',
  'Vignette': '暗角',
  'Software (libx264)': '软件编码（libx264）',
  'Local whisper.cpp': '本地 whisper.cpp',
  'Talking-head provider (any submit-poll-download API)': '数字人服务（任意提交-轮询-下载接口）',
  'Checking…': '检测中…',
  'Engine default': '引擎默认',
  'optional': '可选',
  'Starting…': '启动中…',

  // Explanatory copy — the paragraphs that tell people what the app will and will not do.
  'One authorized image with one clear adult face. It never leaves this machine unless you configure a remote provider and approve the upload.':
    '一张已授权的图片，包含一张清晰的成年人面孔。除非你配置了远程服务商并批准上传，否则它不会离开本机。',
  'Optional. Without it, caption timings come from the built-in aligner, which knows each segment\'s exact text and measured duration.':
    '可选。不使用时，字幕时间来自内置对齐器 —— 它已知每段的确切文本和实测时长。',
  'Stored in a local file readable only by your user account, never in a job folder or a report. Environment variables of the same name take priority.':
    '保存在仅你的用户账户可读的本地文件中，绝不写入任务文件夹或报告。同名环境变量优先。',
  'The motion plate animates your still image. It is not a lip-synced talking head, and every job record says which one produced the track.':
    '动态画面基于你的静态图片生成动画。它不是口型同步的数字人；每条任务记录都会写明画面由哪一种方式产生。',
  'A model is configured, so the script will be fully drafted from your topic.':
    '已配置模型，脚本将根据你的主题完整生成。',
  'No model key is set, so the built-in outline writer will build the structure and you edit the narration. Add a key in Settings for a fully drafted script.':
    '未设置模型密钥，将由内置大纲生成器搭建结构，旁白由你编辑。在设置中添加密钥即可获得完整脚本。',

  // Explanatory copy
  'Used only where it proves or clarifies a spoken point. Assigned to body beats, never the hook or the close.':
    '仅在能佐证或阐明所讲内容时使用。只分配给正文段落，不用于开场和结尾。',
  'Select a job to see its state, artifacts, and run log.': '选择一个任务以查看其状态、产物和运行日志。',
  'Hardware encoders are probed before use and fall back to software automatically if they fail mid-render.':
    '硬件编码器会先行探测，若渲染中途失败将自动回退到软件编码。',
  'Placeholders:': '占位符：',

  // Placeholders
  'Explain context engineering in one minute': '用一分钟讲清上下文工程',
  'Paste the narration. Headings become chapters; everything else is spoken.':
    '粘贴旁白。标题会成为章节，其余内容将被朗读。',
  'Leave blank for no promo copy': '留空则不添加推广文案',
  'leave blank to auto-detect': '留空则自动检测',
  'leave blank if unknown': '未知则留空',
};

const I18N = { en: {}, zh: ZH };

function translationsFor(language) {
  return I18N[language] || I18N.en;
}

/**
 * Walks the document translating text nodes and the attributes users actually read.
 * The original English is kept on the node so switching back is lossless rather than a
 * second translation in the opposite direction.
 */
function applyLanguage(language) {
  const table = translationsFor(language);
  document.documentElement.lang = language === 'zh' ? 'zh-CN' : 'en';

  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);

  nodes.forEach((node) => {
    if (node.parentElement && node.parentElement.closest('script, style, canvas')) return;

    // Both the trimmed source string and the whole original value are kept. Rebuilding from the
    // original every time is what makes switching back lossless: replacing the *current* text
    // would look for English that is no longer there once the node has been translated once.
    if (node.__en === undefined) {
      const trimmed = node.nodeValue.trim();
      if (!trimmed) return;
      node.__en = trimmed;
      node.__enFull = node.nodeValue;
    }

    const replacement = table[node.__en];
    // Rebuild from the original so the surrounding whitespace survives untouched.
    node.nodeValue = node.__enFull.replace(node.__en, replacement || node.__en);
  });

  document.querySelectorAll('[placeholder], [title]').forEach((element) => {
    ['placeholder', 'title'].forEach((attribute) => {
      const value = element.getAttribute(attribute);
      if (value === null) return;
      const key = `__en_${attribute}`;
      if (element[key] === undefined) element[key] = value;
      element.setAttribute(attribute, table[element[key]] || element[key]);
    });
  });
}

function initLanguage() {
  const select = $('uiLanguage');
  if (!select) return;

  let stored = null;
  try {
    // The key was renamed with the product; reading the old one keeps an existing
    // choice rather than silently flipping someone back to their browser's language.
    stored = localStorage.getItem('hela_ui_language')
      || localStorage.getItem('lanshu_ui_language');
  } catch (error) {
    // A browser with site data blocked still gets a working studio, just not a remembered choice.
    stored = null;
  }

  // Precedence matters here: a stored choice of 'en' must win over a zh-CN browser.
  const detected = (navigator.language || '').toLowerCase().startsWith('zh') ? 'zh' : 'en';
  select.value = stored || detected;
  applyLanguage(select.value);

  select.addEventListener('change', () => {
    applyLanguage(select.value);
    try {
      localStorage.setItem('hela_ui_language', select.value);
    } catch (error) {
      /* remembering the choice is a convenience, not a requirement */
    }
  });
}

// Re-translate after the app injects markup, so dynamic panels are not left in English.
const untranslatedRender = window.requestAnimationFrame.bind(window);
function retranslate() {
  const select = $('uiLanguage');
  if (select && select.value !== 'en') untranslatedRender(() => applyLanguage(select.value));
}

initLanguage();
