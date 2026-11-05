// LocalStream web UI. Data comes from the /api endpoints as JSON; the page is built with DOM APIs
// (textContent / setAttribute), so file names are never interpreted as HTML or script.
'use strict';

const svg = (inner, fill) => `<svg width="24" height="24" fill="${fill ? 'currentColor' : 'none'}" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" viewBox="0 0 24 24">${inner}</svg>`;
const ICONS = {
  'folder': svg("<path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/>"),
  'corner-up-left': svg("<polyline points='9 14 4 9 9 4'/><path d='M20 20v-7a4 4 0 0 0-4-4H4'/>"),
  'search': svg("<circle cx='11' cy='11' r='8'/><line x1='21' y1='21' x2='16.65' y2='16.65'/>"),
  'upload': svg("<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='17 8 12 3 7 8'/><line x1='12' y1='3' x2='12' y2='15'/>"),
  'download': svg("<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><polyline points='7 10 12 15 17 10'/><line x1='12' y1='15' x2='12' y2='3'/>"),
  'grid': svg("<rect x='3' y='3' width='7' height='7'/><rect x='14' y='3' width='7' height='7'/><rect x='14' y='14' width='7' height='7'/><rect x='3' y='14' width='7' height='7'/>"),
  'play': svg("<circle cx='12' cy='12' r='10'/><polygon points='10 8 16 12 10 16 10 8'/>"),
  'play-solid': svg("<polygon points='6 4 20 12 6 20 6 4'/>", true),
  'pause-solid': svg("<rect x='6' y='4' width='4' height='16'/><rect x='14' y='4' width='4' height='16'/>", true),
  'skip-back': svg("<polygon points='19 20 9 12 19 4 19 20'/><line x1='5' y1='19' x2='5' y2='5'/>"),
  'skip-forward': svg("<polygon points='5 4 15 12 5 20 5 4'/><line x1='19' y1='5' x2='19' y2='19'/>"),
  'rewind': svg("<polygon points='11 19 2 12 11 5 11 19'/><polygon points='22 19 13 12 22 5 22 19'/>"),
  'fast-forward': svg("<polygon points='13 19 22 12 13 5 13 19'/><polygon points='2 19 11 12 2 5 2 19'/>"),
  'square': svg("<rect x='5' y='5' width='14' height='14'/>"),
  'maximize': svg("<path d='M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3'/>"),
  'x': svg("<line x1='18' y1='6' x2='6' y2='18'/><line x1='6' y1='6' x2='18' y2='18'/>"),
  'tv': svg("<rect x='2' y='7' width='20' height='13' rx='2'/><polyline points='17 2 12 7 7 2'/>"),
  'chevron-left': svg("<polyline points='15 18 9 12 15 6'/>"),
  'chevron-right': svg("<polyline points='9 18 15 12 9 6'/>"),
  'server': svg("<rect x='2' y='2' width='20' height='8' rx='2' ry='2'/><rect x='2' y='14' width='20' height='8' rx='2' ry='2'/><line x1='6' y1='6' x2='6.01' y2='6'/><line x1='6' y1='18' x2='6.01' y2='18'/>"),
  'drive': svg("<rect x='2' y='7' width='20' height='14' rx='2' ry='2'/><path d='M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16'/>"),
  'moon': svg("<path d='M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z'/>"),
  'check-square': svg("<polyline points='9 11 12 14 22 4'/><path d='M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11'/>"),
  'folder-plus': svg("<path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/><line x1='12' y1='11' x2='12' y2='17'/><line x1='9' y1='14' x2='15' y2='14'/>"),
  'copy': svg("<rect x='9' y='9' width='13' height='13' rx='2' ry='2'/><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/>"),
  'scissors': svg("<circle cx='6' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><line x1='20' y1='4' x2='8.12' y2='15.88'/><line x1='14.47' y1='14.48' x2='20' y2='20'/><line x1='8.12' y1='8.12' x2='12' y2='12'/>"),
  'clipboard': svg("<path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/><rect x='8' y='2' width='8' height='4' rx='1' ry='1'/>"),
  'trash': svg("<polyline points='3 6 5 6 21 6'/><path d='M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2'/>"),
  'video': svg("<rect x='2' y='2' width='20' height='20' rx='2.18' ry='2.18'/><line x1='7' y1='2' x2='7' y2='22'/><line x1='17' y1='2' x2='17' y2='22'/><line x1='2' y1='12' x2='22' y2='12'/><line x1='2' y1='7' x2='7' y2='7'/><line x1='2' y1='17' x2='7' y2='17'/><line x1='17' y1='17' x2='22' y2='17'/><line x1='17' y1='7' x2='22' y2='7'/>"),
  'audio': svg("<path d='M9 18V5l12-2v13'/><circle cx='6' cy='18' r='3'/><circle cx='18' cy='16' r='3'/>"),
  'user': svg("<path d='M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2'/><circle cx='12' cy='7' r='4'/>"),
  'disc': svg("<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='3'/>"),
  'image': svg("<rect x='3' y='3' width='18' height='18' rx='2' ry='2'/><circle cx='8.5' cy='8.5' r='1.5'/><polyline points='21 15 16 10 5 21'/>"),
  'archive': svg("<path d='M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z'/><polyline points='3.27 6.96 12 12.01 20.73 6.96'/><line x1='12' y1='22.08' x2='12' y2='12'/>"),
  'document': svg("<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/><line x1='16' y1='13' x2='8' y2='13'/><line x1='16' y1='17' x2='8' y2='17'/><polyline points='10 9 9 9 8 9'/>"),
  'link': svg("<path d='M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71'/><path d='M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71'/>"),
  'share': svg("<circle cx='18' cy='5' r='3'/><circle cx='6' cy='12' r='3'/><circle cx='18' cy='19' r='3'/><line x1='8.59' y1='13.51' x2='15.42' y2='17.49'/><line x1='15.41' y1='6.51' x2='8.59' y2='10.49'/>"),
  'log-out': svg("<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><polyline points='16 17 21 12 16 7'/><line x1='21' y1='12' x2='9' y2='12'/>"),
  'check': svg("<polyline points='20 6 9 17 4 12'/>"),
  'heart': svg("<path d='M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z'/>"),
  'heart-solid': svg("<path d='M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z'/>", true),
  'list-plus': svg("<line x1='3' y1='6' x2='15' y2='6'/><line x1='3' y1='12' x2='15' y2='12'/><line x1='3' y1='18' x2='11' y2='18'/><line x1='18' y1='14' x2='18' y2='22'/><line x1='14' y1='18' x2='22' y2='18'/>"),
  'list': svg("<line x1='8' y1='6' x2='21' y2='6'/><line x1='8' y1='12' x2='21' y2='12'/><line x1='8' y1='18' x2='21' y2='18'/><line x1='3' y1='6' x2='3.01' y2='6'/><line x1='3' y1='12' x2='3.01' y2='12'/><line x1='3' y1='18' x2='3.01' y2='18'/>"),
  'shuffle': svg("<polyline points='16 3 21 3 21 8'/><line x1='4' y1='20' x2='21' y2='3'/><polyline points='21 16 21 21 16 21'/><line x1='15' y1='15' x2='21' y2='21'/><line x1='4' y1='4' x2='9' y2='9'/>"),
  'repeat': svg("<polyline points='17 1 21 5 17 9'/><path d='M3 11V9a4 4 0 0 1 4-4h14'/><polyline points='7 23 3 19 7 15'/><path d='M21 13v2a4 4 0 0 1-4 4H3'/>"),
  'mic': svg("<path d='M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z'/><path d='M19 10v2a7 7 0 0 1-14 0v-2'/><line x1='12' y1='19' x2='12' y2='23'/><line x1='8' y1='23' x2='16' y2='23'/>"),
  'type': svg("<polyline points='4 7 4 4 20 4 20 7'/><line x1='9' y1='20' x2='15' y2='20'/><line x1='12' y1='4' x2='12' y2='20'/>"),
  'cc-search': svg("<rect x='2' y='5' width='15' height='13' rx='2'/><path d='M8 10.5a1.8 1.8 0 1 0 0 2.6M13 10.5a1.8 1.8 0 1 0 0 2.6'/><circle cx='18.5' cy='16.5' r='3'/><line x1='20.7' y1='18.7' x2='22.5' y2='20.5'/>"),
  'arrow-up': svg("<line x1='12' y1='19' x2='12' y2='5'/><polyline points='5 12 12 5 19 12'/>"),
  'arrow-down': svg("<line x1='12' y1='5' x2='12' y2='19'/><polyline points='19 12 12 19 5 12'/>"),
  'edit': svg("<path d='M12 20h9'/><path d='M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z'/>"),
  'more': svg("<circle cx='12' cy='5' r='1.5'/><circle cx='12' cy='12' r='1.5'/><circle cx='12' cy='19' r='1.5'/>", true),
  'pip': svg("<rect x='2' y='4' width='20' height='16' rx='2'/><rect x='12' y='11' width='8' height='6' rx='1'/>"),
  'cloud-off': svg("<path d='M22.61 16.95A5 5 0 0 0 18 10h-1.26a8 8 0 0 0-7.05-6M5 5a8 8 0 0 0 4 15h9a5 5 0 0 0 1.7-.3'/><line x1='1' y1='1' x2='23' y2='23'/>"),
  'film': svg("<rect x='2' y='2' width='20' height='20' rx='2.18' ry='2.18'/><line x1='7' y1='2' x2='7' y2='22'/><line x1='17' y1='2' x2='17' y2='22'/><line x1='2' y1='12' x2='22' y2='12'/>"),
  'file': svg("<path d='M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z'/><polyline points='13 2 13 9 20 9'/>"),
};

const EXT_ICON = [
  [['.mp4', '.mkv', '.avi', '.mov', '.webm', '.wmv', '.m4v', '.ts', '.mpg', '.mpeg', '.m2ts', '.mts', '.3gp', '.flv', '.vob', '.ogv', '.divx', '.asf'], 'video'],
  [['.mp3', '.wav', '.flac', '.ogg', '.m4a', '.aac', '.opus', '.mpga', '.wma', '.aiff', '.aif', '.ape', '.wv', '.mka', '.m4b', '.oga', '.amr', '.ac3', '.dts', '.alac', '.weba', '.mp2'], 'audio'],
  [['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp'], 'image'],
  [['.zip', '.rar', '.7z', '.tar', '.gz'], 'archive'],
  [['.pdf', '.doc', '.docx', '.txt', '.xlsx'], 'document'],
];

const $ = (id) => document.getElementById(id);
const store = {
  get(area, key, fallback) { try { return window[area].getItem(key) ?? fallback; } catch { return fallback; } },
  set(area, key, value) { try { window[area].setItem(key, value); } catch { /* storage blocked */ } },
  remove(area, key) { try { window[area].removeItem(key); } catch { } },
};

let state = null;                 // last /api/list response
const query = new URLSearchParams(location.search);
const currentPath = query.get('path') || '';
const view = query.get('view') || '';   // '' = folders, or music / movies / shows / photos / favorites / playlists
let me = null;                    // /api/me: who is signed in and what they may do

// ------------------------------------------------------------------ helpers

function icon(name) {
  const span = document.createElement('span');
  span.setAttribute('data-icon', name);
  span.innerHTML = ICONS[name] || '';   // static markup from the table above, never user data
  return span;
}

function setIcon(button, name) {
  const span = button.querySelector('[data-icon]');
  if (span && span.getAttribute('data-icon') !== name) { span.setAttribute('data-icon', name); span.innerHTML = ICONS[name] || ''; }
}

function el(tag, attrs = {}, ...children) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children) if (c) e.append(c);
  return e;
}

function folderHref(path) { return '/?path=' + encodeURIComponent(path); }

function formatSize(bytes) {
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  let len = bytes, order = 0;
  while (len >= 1024 && order < sizes.length - 1) { order++; len /= 1024; }
  return `${Math.round(len * 100) / 100} ${sizes[order]}`;
}

function formatTime(s) {
  if (!isFinite(s) || s < 0) s = 0;
  s = Math.floor(s);
  const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = s % 60;
  return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(sec).padStart(2, '0');
}

function extOf(name) { return (name.match(/\.[^.]+$/) || [''])[0].toLowerCase(); }

function fileIcon(name) {
  const ext = extOf(name);
  for (const [exts, iconName] of EXT_ICON) if (exts.includes(ext)) return iconName;
  return 'file';
}

function isPlayable(name) { return ['video', 'audio', 'image'].includes(fileIcon(name)); }

function baseName(path) { return path.split(/[\\/]/).filter(Boolean).pop() || path; }

function showMessage(text, isError) {
  const m = $('message');
  m.textContent = text;
  m.className = 'message' + (isError ? ' error' : '');
  m.hidden = !text;
}

async function post(url) {
  const res = await fetch(url, { method: 'POST' });
  return res;
}

async function postJson(url, data, keepalive) {
  return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data), keepalive: !!keepalive });
}

function explainFailure(res) {
  if (res.status === 403) return 'Not allowed. Check that you are logged in as admin and that the item is inside a shared folder.';
  if (res.status === 409) return 'Something with that name already exists.';
  if (res.status === 401) return 'Your login expired. Reload the page.';
  return `The server returned an error (${res.status}).`;
}

// ------------------------------------------------------------------ rendering: folders

function renderBreadcrumbs() {
  const nav = $('breadcrumbs');
  nav.replaceChildren(el('a', { href: '/' }, icon('server'), document.createTextNode(' Home')));
  for (const c of state.crumbs) {
    nav.append(el('span', { class: 'sep', text: '/' }), el('a', { href: folderHref(c.path), text: c.name }));
  }
}

function renderTopBar() {
  $('title').replaceChildren(icon(state.isRoot ? 'server' : 'folder'), document.createTextNode(' ' + state.name));
  document.title = state.isRoot ? 'LocalStream' : `${state.name} - LocalStream`;
  const isAdmin = state.role === 'Admin';
  $('btnSelectMode').hidden = state.isRoot;
  // Upload and "New folder" for accounts allowed to add files; copy/cut/delete only for admins.
  $('adminControls').hidden = state.isRoot || !state.canUpload;
  $('btnNewFolder').hidden = !state.canUpload;
  document.querySelectorAll('.admin-only').forEach(b => b.hidden = !isAdmin);
  $('searchInput').placeholder = state.isRoot ? 'Search all folders...' : 'Search...';
  updatePasteButton();
}

function makeItem({ name, type, size, href, newTab }) {
  const item = el('div', { class: 'item' });
  item.dataset.name = name.toLowerCase();
  item.dataset.type = type === 'file' ? 'file' : 'folder';
  item.dataset.size = size ?? 0;
  if (href) item.dataset.href = href;
  if (newTab) item.dataset.newTab = '1';
  return item;
}

function actionLink(iconName, title, href, extra = {}) {
  return el('a', { class: 'btn-action', title, href, ...extra }, icon(iconName));
}

function actionButton(iconName, title, onclick, danger) {
  return el('button', { class: 'btn-action' + (danger ? ' btn-danger-action' : ''), title, type: 'button', onclick }, icon(iconName));
}

function renderLibrary(lib) {
  const href = folderHref(lib.path);
  const item = makeItem({ name: lib.name, type: 'folder', size: 0, href });
  const meta = el('span', { class: 'meta', text: '…' });
  item.append(
    el('div', { class: 'preview-box' }, icon('drive')),
    el('div', { class: 'details' },
      el('div', { class: 'details-wrapper' },
        el('div', { class: 'name-wrap' }, el('a', { href, class: 'name', text: lib.name }), meta)),
      el('div', { class: 'actions' },
        actionLink('folder', 'Open library', href),
        actionLink('download', 'Download library as zip', '/zip?path=' + encodeURIComponent(lib.path)))));
  queueSize(lib.path, item, meta);
  return item;
}

function adminActions(path, isAdmin) {
  const share = state && state.canShare ? [actionButton('share', 'Share a link', () => openShare(path))] : [];
  if (!isAdmin) return share;
  return [...share,
    actionButton('copy', 'Copy', () => setClipboard('copy', [path])),
    actionButton('scissors', 'Cut', () => setClipboard('move', [path])),
    actionButton('trash', 'Delete', () => deletePaths([path]), true),
  ];
}

function checkbox(fullPath, zipName) {
  const chk = el('input', { type: 'checkbox', class: 'checkbox file-check' });
  chk.dataset.fullpath = fullPath;
  chk.dataset.zipname = zipName;   // only files can go in the zip-selected download
  chk.addEventListener('click', (e) => { e.stopPropagation(); syncSelected(chk); });
  return chk;
}

function renderFolder(f, isAdmin) {
  const href = folderHref(f.path);
  const item = makeItem({ name: f.name, type: 'folder', size: 0, href });
  const meta = el('span', { class: 'meta', text: '…' });
  item.append(
    el('div', { class: 'preview-box' }, icon('folder')),
    el('div', { class: 'details' },
      el('div', { class: 'details-wrapper' },
        checkbox(f.path, ''),
        el('div', { class: 'name-wrap' }, el('a', { href, class: 'name', text: f.name }), meta)),
      el('div', { class: 'actions' },
        actionLink('folder', 'Open folder', href),
        actionLink('download', 'Download folder as zip', '/zip?path=' + encodeURIComponent(f.path)),
        ...adminActions(f.path, isAdmin))));
  queueSize(f.path, item, meta);
  return item;
}

function preview(f) {
  const box = el('div', { class: 'preview-box' }, icon(fileIcon(f.name)));
  if (f.thumb) {
    const img = el('img', { src: f.thumb, class: 'preview-img', loading: 'lazy', alt: '' });
    img.addEventListener('error', () => img.remove());
    box.append(img);
  }
  return box;
}

function renderFile(f, isAdmin) {
  const playable = isPlayable(f.name);
  const item = makeItem({ name: f.name, type: 'file', size: f.size, href: f.file });
  item.dataset.path = f.path;
  if (playable) item.dataset.play = '1';
  if (MY.watched.has(f.path)) item.classList.add('watched');
  item.append(
    preview(f),
    el('div', { class: 'details' },
      el('div', { class: 'details-wrapper' },
        checkbox(f.path, f.name),
        el('div', { class: 'name-wrap' },
          el('a', { href: f.file, target: '_blank', rel: 'noopener', class: 'name', text: f.name }),
          el('span', { class: 'meta', text: formatSize(f.size) }))),
      el('div', { class: 'actions' },
        playable ? actionButton('play', 'Play / view', () => openMedia(f.path))
                 : actionLink('play', 'Open', f.file, { target: '_blank', rel: 'noopener' }),
        playable ? actionButton('tv', 'Play on TV', () => openTvPicker({ path: f.path, name: f.name })) : null,
        actionLink('download', 'Download', f.file, { download: f.name }),
        ...adminActions(f.path, isAdmin))));
  return item;
}

function renderUpItem() {
  const href = state.parent ? folderHref(state.parent) : '/';
  const item = el('div', { class: 'item up-item' });
  item.dataset.href = href;
  item.append(
    el('div', { class: 'preview-box' }, icon('corner-up-left')),
    el('div', { class: 'details' }, el('div', { class: 'details-wrapper' },
      el('div', {}, el('a', { class: 'name', href, text: '..' }), el('span', { class: 'meta', text: 'Go Up' })))));
  return item;
}

function render() {
  renderBreadcrumbs();
  renderTopBar();
  const container = $('container');
  const isAdmin = state.role === 'Admin';
  const nodes = [];

  if (state.isRoot) {
    for (const lib of state.items) nodes.push(lib.type === 'file' ? renderFile(lib, false) : renderLibrary(lib));
    if (state.items.length === 0) showMessage(state.guest ? 'This link has nothing to show any more.' : 'No folders are shared yet. Add one in the LocalStream app.');
    if (!state.guest) loadHome();
  } else {
    nodes.push(renderUpItem());
    for (const it of state.items) nodes.push(it.type === 'folder' ? renderFolder(it, isAdmin) : renderFile(it, isAdmin));
    if (state.items.length === 0) showMessage('This folder is empty.');
  }
  container.replaceChildren(...nodes);
  sortItems();
  filterItems();
}

// ------------------------------------------------------------------ home: continue watching, recently added

function card(item, { progress, subtitle, onclick, onremove } = {}) {
  const c = el('div', { class: 'card', title: item.name, tabindex: '0' });
  const art = preview(item);
  if (progress) {
    const bar = el('div', { class: 'progress' }, el('div'));
    bar.firstChild.style.width = Math.max(2, Math.min(100, progress * 100)) + '%';
    art.append(bar);
  }
  c.append(art, el('div', { class: 'card-text' }, el('div', { class: 'name', text: item.name.replace(/\.[^.]+$/, '') }), el('div', { class: 'meta', text: subtitle || '' })));
  if (onremove) c.append(el('button', { class: 'card-remove', title: 'Remove from Continue watching', type: 'button', onclick: (e) => { e.stopPropagation(); onremove(c); } }, icon('x')));
  c.addEventListener('click', onclick);
  c.addEventListener('keydown', (e) => { if (e.key === 'Enter') onclick(); });
  return c;
}

async function loadHome() {
  try {
    const res = await fetch('/api/home');
    if (!res.ok) return;
    const home = await res.json();
    const cont = home.continue.map(c => card(c.item, {
      progress: c.duration ? c.position / c.duration : 0,
      subtitle: `${formatTime(c.duration - c.position)} left`,
      onclick: () => openMedia(c.item.path),
      onremove: async (node) => { await postJson('/api/progress', { path: c.item.path, action: 'remove' }); node.remove(); if (!$('continueRow').children.length) $('homeContinue').hidden = true; },
    }));
    $('continueRow').replaceChildren(...cont);
    $('homeContinue').hidden = cont.length === 0;
    const recent = home.recent.map(i => card(i, { subtitle: baseName(i.folder || ''), onclick: () => openMedia(i.path) }));
    $('recentRow').replaceChildren(...recent);
    $('homeRecent').hidden = recent.length === 0;
    $('librariesHead').hidden = cont.length + recent.length === 0;
  } catch { /* home rows are optional */ }
}

// ------------------------------------------------------------------ search across all folders

let searchTimer = 0, searchSeq = 0;

function onSearchInput() {
  filterItems();
  clearTimeout(searchTimer);
  const q = $('searchInput').value.trim();
  if (q.length < 2) { searchSeq++; $('searchResults').hidden = true; $('searchList').replaceChildren(); return; }
  searchTimer = setTimeout(() => searchEverywhere(q), 300);
}

async function searchEverywhere(q) {
  const seq = ++searchSeq;
  try {
    const res = await fetch('/api/search?q=' + encodeURIComponent(q));
    if (!res.ok || seq !== searchSeq) return;
    const data = await res.json();
    const isAdmin = state && state.role === 'Admin';
    const rows = data.items.map(i => {
      const node = i.type === 'folder' ? renderFolder(i, false) : renderFile(i, isAdmin);
      const where = node.querySelector('.meta');
      if (where && i.folder) where.textContent = (i.type === 'file' ? formatSize(i.size) + ' · ' : '') + 'in ' + baseName(i.folder);
      node.querySelectorAll('.file-check').forEach(c => c.remove());
      return node;
    });
    $('searchTitle').textContent = `In all folders: "${q}"`;
    $('searchNote').textContent = data.items.length === 0 ? 'Nothing found.' : data.truncated ? `Showing the first ${data.items.length}.` : `${data.items.length} found`;
    $('searchList').replaceChildren(...rows);
    $('searchResults').hidden = false;
  } catch { /* offline */ }
}

// ------------------------------------------------------------------ music section

async function loadMusic() {
  const artist = query.get('artist'), album = query.get('album'), albums = query.get('all') === 'albums', songs = query.get('all') === 'songs';
  const params = new URLSearchParams();
  if (artist) params.set('artist', artist);
  if (album) params.set('album', album);
  if (albums && !artist) params.set('view', 'albums');
  if (songs && !artist && !album) params.set('view', 'songs');
  document.title = 'Music - LocalStream';
  $('btnSelectMode').hidden = true; $('sortSelect').hidden = true; $('adminControls').hidden = true;
  $('searchInput').placeholder = 'Search all folders...';
  const nav = $('breadcrumbs');
  nav.replaceChildren(el('a', { href: '/' }, icon('server'), document.createTextNode(' Home')), el('span', { class: 'sep', text: '/' }), el('a', { href: '/?view=music', text: 'Music' }));
  if (artist) nav.append(el('span', { class: 'sep', text: '/' }), el('a', { href: '/?view=music&artist=' + encodeURIComponent(artist), text: artist }));
  if (album) nav.append(el('span', { class: 'sep', text: '/' }), el('span', { class: 'crumb-text', text: album }));
  $('title').replaceChildren(icon('audio'), document.createTextNode(' ' + (album || artist || 'Music')));

  const res = await fetch('/api/music?' + params);
  if (!res.ok) { showMessage(explainFailure(res), true); return; }
  const data = await res.json();
  state = { isRoot: false, role: 'Viewer', crumbs: [], items: [] };
  const c = $('container');
  if (!data.complete) showMessage('Still reading the tags of your music. Reload in a moment to see everything.');

  const tabs = (active) => el('div', { class: 'music-tabs' },
    el('a', { class: 'tab' + (active === 'songs' ? ' active' : ''), href: '/?view=music&all=songs', text: 'Songs' }),
    el('a', { class: 'tab' + (active === 'artists' ? ' active' : ''), href: '/?view=music', text: 'Artists' }),
    el('a', { class: 'tab' + (active === 'albums' ? ' active' : ''), href: '/?view=music&all=albums', text: 'Albums' }));

  if (data.level === 'songs') {
    c.before(tabs('songs'));
    if (!data.tracks.length) { fixLayout(c, 'songs'); showMessage('No music found in your shared folders.'); return; }
    renderSongs(c, data.tracks);
    return;
  }

  if (data.level === 'artists') {
    c.before(tabs('artists'));
    c.replaceChildren(...data.artists.map(a => musicCard(a.name, `${a.albums} album${a.albums === 1 ? '' : 's'} · ${a.tracks} tracks`, a.cover, 'user',
      '/?view=music&artist=' + encodeURIComponent(a.name))));
    if (!data.artists.length) showMessage('No music found in your shared folders.');
  } else if (data.level === 'albums') {
    if (!artist) c.before(tabs('albums'));
    c.replaceChildren(...data.albums.map(a => musicCard(a.name, `${artist ? '' : a.artist + ' · '}${a.year ? a.year + ' · ' : ''}${a.tracks} tracks`, a.cover, 'disc',
      '/?view=music&artist=' + encodeURIComponent(a.artist) + '&album=' + encodeURIComponent(a.name))));
  } else {
    const tracks = data.tracks;
    const queue = tracks.map(t => ({ path: t.item.path, title: t.title, artist: t.artist, album: t.album, thumb: t.item.thumb }));
    fixLayout(c, 'songs');
    const head = el('div', { class: 'album-head' });
    if (tracks[0]) {
      const cover = el('img', { src: tracks[0].item.thumb, alt: '' }); cover.addEventListener('error', () => cover.remove());
      head.append(el('div', { class: 'album-cover' }, icon('disc'), cover));
    }
    head.append(el('div', {}, el('h2', { text: album }), el('div', { class: 'meta', text: `${artist || (tracks[0] && tracks[0].artist) || ''} · ${tracks.length} tracks` }),
      el('button', { class: 'btn btn-primary', type: 'button', onclick: () => playQueue(queue, 0) }, icon('play-solid'), document.createTextNode(' Play album'))));
    c.before(head);
    c.replaceChildren(...tracks.map((t, i) => songRow(t, i, queue, false)));
  }
}

// ------------------------------------------------------------------ the Songs page: sorting, A-Z, lazy rows

const SONG_SORTS = {
  title:  { label: 'Title A–Z',        cmp: (a, b) => a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: 'base' }), letter: t => t.title },
  artist: { label: 'Artist',           cmp: (a, b) => a.artist.localeCompare(b.artist, undefined, { sensitivity: 'base' }) || a.album.localeCompare(b.album) || (a.track - b.track) || a.title.localeCompare(b.title), letter: t => t.artist },
  added:  { label: 'Recently added',   cmp: (a, b) => new Date(b.added) - new Date(a.added) },
  plays:  { label: 'Most played',      cmp: (a, b) => (b.plays - a.plays) || a.title.localeCompare(b.title) },
};

function letterOf(s) {
  const ch = (s || '').trim().charAt(0).toUpperCase();
  return ch >= 'A' && ch <= 'Z' ? ch : '#';
}

function renderSongs(c, tracks) {
  let sort = store.get('localStorage', 'songSort', 'title');
  if (!SONG_SORTS[sort]) sort = 'title';
  const S = SONG_SORTS[sort];
  const list = tracks.slice().sort(S.cmp);
  const queue = list.map(t => ({ path: t.item.path, title: t.title, artist: t.artist, album: t.album, thumb: t.item.thumb }));
  fixLayout(c, 'songs');

  document.querySelectorAll('.page-tools, .az-bar').forEach(n => n.remove());
  const sortSel = el('select', { class: 'select-dropdown', title: 'Sort songs' }, ...Object.entries(SONG_SORTS).map(([k, v]) => el('option', { value: k, text: v.label })));
  sortSel.value = sort;
  sortSel.addEventListener('change', () => { store.set('localStorage', 'songSort', sortSel.value); renderSongs(c, tracks); window.scrollTo(0, 0); });
  c.before(el('div', { class: 'page-tools' },
    el('span', { class: 'meta', text: `${list.length.toLocaleString()} song${list.length === 1 ? '' : 's'}` }),
    el('button', { class: 'btn btn-primary', type: 'button', onclick: () => playQueue(queue, 0) }, icon('play-solid'), document.createTextNode(' Play all')),
    el('button', { class: 'btn btn-secondary', type: 'button', onclick: () => { playQueue(queue, Math.floor(Math.random() * queue.length)); setTimeout(toggleShuffle, 0); } }, icon('shuffle'), document.createTextNode(' Shuffle')),
    sortSel));

  const lazy = lazyRows(c, list, (t, i) => {
    const row = songRow(t, i, queue, true);
    if (sort === 'plays' && t.plays) row.querySelector('.meta').textContent += ` · played ${t.plays}×`;
    return row;
  });

  // A–Z: jump to the first song of a letter (for the alphabetical sorts).
  if (S.letter) {
    const first = {};
    list.forEach((t, i) => { const l = letterOf(S.letter(t)); if (!(l in first)) first[l] = i; });
    const bar = el('nav', { class: 'az-bar', 'aria-label': 'Jump to letter' }, ...['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'].map(l =>
      el('button', { type: 'button', text: l, disabled: !(l in first), onclick: () => lazy.scrollTo(first[l]) })));
    document.body.append(bar);
  }
}

/**
 * Shows a long list a page at a time: more rows are added as you scroll near the end, so thousands
 * of songs open instantly (phones included). scrollTo(i) renders up to row i and jumps there.
 */
function lazyRows(c, items, make, chunk = 120) {
  let shown = 0;
  c.replaceChildren();
  document.querySelectorAll('.lazy-sentinel').forEach(n => n.remove());
  const sentinel = el('div', { class: 'lazy-sentinel' });
  c.after(sentinel);
  const more = (upTo) => {
    const end = Math.min(items.length, Math.max(shown + chunk, upTo || 0));
    const frag = document.createDocumentFragment();
    for (; shown < end; shown++) frag.append(make(items[shown], shown));
    c.append(frag);
    sentinel.hidden = shown >= items.length;
  };
  more();
  const io = new IntersectionObserver((e) => { if (e.some(x => x.isIntersecting) && shown < items.length) more(); }, { rootMargin: '1200px' });
  io.observe(sentinel);
  return {
    scrollTo(i) {
      if (i >= shown) more(i + chunk);
      const row = c.children[i];
      if (row) row.scrollIntoView({ block: 'start' }); // instant: a smooth scroll over thousands of rows takes seconds
    },
  };
}

/** One song in a list: number (or cover), title, artist · album, and its buttons. */
function songRow(t, i, queue, withCover, onPlay) {
  const row = el('div', { class: 'item track' + (withCover ? ' with-cover' : ''), title: [t.title, t.artist, t.album].filter(Boolean).join(' · ') });
  const cover = el('div', { class: 'queue-thumb light song-cover' }, icon('audio'));
  if (t.item.thumb) { const img = el('img', { src: t.item.thumb, loading: 'lazy', alt: '' }); img.addEventListener('error', () => img.remove()); cover.append(img); }
  const sub = withCover ? [t.artist, t.album].filter(Boolean).join(' · ') : t.artist;
  row.append(el('span', { class: 'track-no', text: t.track ? String(t.track) : String(i + 1) }), cover,
    el('div', { class: 'details' }, el('div', { class: 'name-wrap' }, el('span', { class: 'name', text: t.title }), el('span', { class: 'meta', text: sub })),
      el('div', { class: 'actions' },
        actionButton('list-plus', 'Add to a playlist', (e) => { e.stopPropagation(); openPlaylistPicker([t.item.path], t.title); }),
        actionButton('tv', 'Play on TV', (e) => { e.stopPropagation(); openTvPicker({ path: t.item.path, name: t.title }); }),
        actionLink('download', 'Download', t.item.file, { download: t.item.name }))));
  row.addEventListener('click', (e) => { if (!e.target.closest('.btn-action')) onPlay ? onPlay() : playQueue(queue, i); });
  return row;
}

function musicCard(name, subtitle, cover, fallbackIcon, href) {
  const item = el('a', { class: 'item music-card', href });
  const box = el('div', { class: 'preview-box square' }, icon(fallbackIcon));
  if (cover) { const img = el('img', { src: cover, class: 'preview-img', loading: 'lazy', alt: '' }); img.addEventListener('error', () => img.remove()); box.append(img); }
  item.append(box, el('div', { class: 'details' }, el('div', { class: 'name-wrap' }, el('span', { class: 'name', text: name }), el('span', { class: 'meta', text: subtitle }))));
  return item;
}

// ------------------------------------------------------------------ folder sizes (loaded lazily)

const sizeQueue = [];
let sizeWorkers = 0;

function queueSize(path, item, meta) {
  sizeQueue.push({ path, item, meta });
  while (sizeWorkers < 3 && sizeQueue.length) runSizeWorker();
}

async function runSizeWorker() {
  sizeWorkers++;
  try {
    while (sizeQueue.length) {
      const job = sizeQueue.shift();
      try {
        const res = await fetch('/api/size?path=' + encodeURIComponent(job.path));
        if (!res.ok) { job.meta.textContent = ''; continue; }
        const { size } = await res.json();
        job.item.dataset.size = size;
        job.meta.textContent = formatSize(size);
        if ($('sortSelect').value.startsWith('size')) sortItems();
      } catch { job.meta.textContent = ''; }
    }
  } finally { sizeWorkers--; }
}

// ------------------------------------------------------------------ sort / filter / view

function sortItems() {
  const val = $('sortSelect').value;
  const c = $('container');
  const up = c.querySelector('.up-item');
  const items = Array.from(c.querySelectorAll(':scope > .item:not(.up-item)'));
  if (view === 'music') return;
  items.sort((a, b) => {
    if (a.dataset.type !== b.dataset.type) return a.dataset.type === 'folder' ? -1 : 1;
    const nA = a.dataset.name, nB = b.dataset.name, sA = +a.dataset.size, sB = +b.dataset.size;
    if (val === 'name-asc') return nA.localeCompare(nB, undefined, { numeric: true });
    if (val === 'name-desc') return nB.localeCompare(nA, undefined, { numeric: true });
    if (val === 'size-desc') return sB - sA;
    return sA - sB;
  });
  if (up) c.append(up);
  items.forEach(i => c.append(i));
}

function filterItems() {
  const q = $('searchInput').value.toLowerCase();
  document.querySelectorAll('#container > .item:not(.up-item)').forEach(i => {
    i.style.display = !i.dataset.name || i.dataset.name.includes(q) ? '' : 'none';
  });
}

/**
 * Library pages have their own grid / list choice (remembered per kind of page), separate from the
 * folders' one: songs and albums, movies and shows (posters), photos, episodes, playlists.
 * The grid / list button switches it and the grid-size menu sets the size in grid mode.
 */
const LAYOUTS = {
  songs:    { def: 'list', grid: 'song-grid',    list: 'list' },
  posters:  { def: 'grid', grid: 'posters',      list: 'list poster-list' },
  timeline: { def: 'grid', grid: 'timeline',     list: 'timeline photo-list' },
  episodes: { def: 'list', grid: 'episode-grid', list: 'list' },
  playlist: { def: 'list', grid: 'song-grid',    list: 'list' },
};

function fixLayout(c, kind) {
  const L = LAYOUTS[kind];
  c.dataset.layout = kind;
  if (!L) { // a fixed layout
    c.className = 'container ' + kind;
    document.querySelector('.grid-select-wrapper').hidden = true;
    $('btnView').hidden = true;
    return;
  }
  const grid = store.get('localStorage', 'view:' + kind, kind === 'songs' ? store.get('localStorage', 'songView', L.def) : L.def) === 'grid';
  c.className = 'container ' + (grid ? L.grid : L.list);
  document.querySelector('.grid-select-wrapper').hidden = !grid;
  $('btnView').hidden = false;
  setIcon($('btnView'), grid ? 'list' : 'grid');
  $('btnView').title = grid ? 'Show as a list' : 'Show as a grid';
}

function toggleLayout(c) {
  const kind = c.dataset.layout, L = LAYOUTS[kind];
  if (!L) return false;
  const grid = c.className === 'container ' + L.grid;
  store.set('localStorage', 'view:' + kind, grid ? 'list' : 'grid');
  fixLayout(c, kind);
  return true;
}

function applyViewSettings() {
  const mode = store.get('localStorage', 'viewMode', 'grid');
  const theme = store.get('localStorage', 'theme', matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  const gSize = store.get('localStorage', 'gridSize', '200');
  if (!$('container').dataset.layout) $('container').className = 'container ' + mode;
  document.body.classList.toggle('list-view', mode === 'list');
  document.documentElement.setAttribute('data-theme', theme);
  $('gridSizeSelect').value = gSize;
  document.documentElement.style.setProperty('--grid-size', gSize + 'px');
}

// ------------------------------------------------------------------ selection

function toggleSelectMode() {
  const on = document.body.classList.toggle('select-mode');
  $('btnSelectMode').classList.toggle('active', on);
  if (!on) {
    document.querySelectorAll('.file-check').forEach(c => { c.checked = false; });
    document.querySelectorAll('.item.selected').forEach(i => i.classList.remove('selected'));
  }
  updateSelectionBar();
}

function syncSelected(chk) {
  chk.closest('.item').classList.toggle('selected', chk.checked);
  updateSelectionBar();
}

function selectedChecks() { return Array.from(document.querySelectorAll('.file-check:checked')); }

function updateSelectionBar() {
  const count = selectedChecks().length;
  $('selectionBar').classList.toggle('visible', count > 0);
  $('selCount').textContent = `${count} item(s) selected`;
}

function onItemClick(e) {
  const item = e.target.closest('.item');
  if (!item || e.target.closest('.btn-action') || item.classList.contains('track') || item.classList.contains('music-card')) return;
  if (document.body.classList.contains('select-mode') && !item.classList.contains('up-item')) {
    const chk = item.querySelector('.file-check');
    if (!chk || e.target === chk) return;
    e.preventDefault(); // in select mode a click on the name selects instead of opening
    chk.checked = !chk.checked;
    syncSelected(chk);
    return;
  }
  if (item.dataset.play && item.dataset.path) { e.preventDefault(); openMedia(item.dataset.path); return; }
  if (e.target.closest('a')) return; // let the link do its thing
  if (item.dataset.href) window.location = item.dataset.href;
}

// ------------------------------------------------------------------ clipboard (copy / cut / paste)

function setClipboard(action, paths) {
  if (!paths.length) return;
  store.set('sessionStorage', 'ls_clip_action', action);
  store.set('sessionStorage', 'ls_clip_paths', JSON.stringify(paths));
  if (document.body.classList.contains('select-mode')) toggleSelectMode();
  updatePasteButton();
}

function clipboardPaths() {
  try { return JSON.parse(store.get('sessionStorage', 'ls_clip_paths', '[]')) || []; } catch { return []; }
}

function updatePasteButton() {
  const btn = $('btnPaste');
  const paths = clipboardPaths();
  if (!state || state.isRoot || state.role !== 'Admin' || paths.length === 0) { btn.hidden = true; return; }
  let label = paths.length === 1 ? baseName(paths[0]) : `(${paths.length})`;
  if (label.length > 25) label = label.substring(0, 25) + '...';
  $('pasteLabel').textContent = 'Paste ' + label;
  btn.hidden = false;
}

async function pasteItems() {
  const action = store.get('sessionStorage', 'ls_clip_action', null);
  const paths = clipboardPaths();
  if (!action || !paths.length) return;
  $('btnPaste').disabled = true;
  const results = await Promise.all(paths.map(p =>
    post(`/${action === 'move' ? 'move' : 'copy'}?src=${encodeURIComponent(p)}&dest=${encodeURIComponent(currentPath)}`)));
  const failed = results.filter(r => !r.ok);
  if (failed.length) alert(`${failed.length} of ${results.length} operations failed. ${explainFailure(failed[0])}`);
  if (action === 'move') store.remove('sessionStorage', 'ls_clip_paths');
  location.reload();
}

// ------------------------------------------------------------------ delete / new folder / upload / download

async function deletePaths(paths) {
  if (!paths.length) return;
  const question = paths.length === 1 ? `Delete "${baseName(paths[0])}"? This cannot be undone.` : `Delete ${paths.length} selected items? This cannot be undone.`;
  if (!confirm(question)) return;
  const results = await Promise.all(paths.map(p => post('/delete?path=' + encodeURIComponent(p))));
  if (results.some(r => r.status === 403)) {
    alert('Delete blocked: enable "Allow Admin to Delete files via Web Interface" in the LocalStream app settings.');
    return;
  }
  const failed = results.filter(r => !r.ok);
  if (failed.length) alert(`${failed.length} item(s) could not be deleted. ${explainFailure(failed[0])}`);
  location.reload();
}

async function createFolder() {
  const name = prompt('Enter new folder name:');
  if (!name || !name.trim()) return;
  const res = await post(`/mkdir?path=${encodeURIComponent(currentPath)}&name=${encodeURIComponent(name.trim())}`);
  if (res.ok) location.reload();
  else alert('Failed to create folder. ' + explainFailure(res));
}

function uploadFiles(files) {
  if (!files.length) return;
  const fd = new FormData();
  for (const f of files) fd.append('files', f, f.name);

  const btn = $('btnUpload'), label = $('uploadLabel');
  btn.disabled = true;
  const xhr = new XMLHttpRequest();
  xhr.open('POST', '/upload?path=' + encodeURIComponent(currentPath));
  xhr.upload.addEventListener('progress', (e) => {
    if (e.lengthComputable) label.textContent = `Uploading ${Math.floor(e.loaded / e.total * 100)}%`;
  });
  xhr.addEventListener('load', () => {
    if (xhr.status >= 200 && xhr.status < 300) location.reload();
    else { alert('Upload failed. ' + explainFailure(xhr)); btn.disabled = false; label.textContent = 'Upload'; }
  });
  xhr.addEventListener('error', () => { alert('Upload failed: connection lost.'); btn.disabled = false; label.textContent = 'Upload'; });
  xhr.send(fd);
}

function downloadSelectedAsZip() {
  const names = selectedChecks().map(c => c.dataset.zipname).filter(Boolean);
  if (!names.length) { alert('Select files to zip (folders can be downloaded with their own zip button).'); return; }
  window.location = `/zip?path=${encodeURIComponent(currentPath)}&files=${encodeURIComponent(names.join('|'))}`;
}

function downloadEach() {
  selectedChecks().forEach((c, i) => {
    const link = c.closest('.item').querySelector('a[download]') || c.closest('.item').querySelector('a[href^="/zip"]');
    if (link) setTimeout(() => link.click(), i * 400); // small gap so browsers don't block the burst
  });
}

// ================================================================== player (video, music, photos)
//
// Files the browser can play are streamed as they are (/file/, seekable). Others (MKV, HEVC,
// AC3...) are converted live by the PC (/stream/?start=). A converted stream always starts at 0,
// so the player keeps the offset itself and seeking restarts the conversion at the new spot.

const P = {
  media: null,        // /api/media response
  converting: false,
  offset: 0,          // seconds into the file where the current converted stream starts
  audioTrack: 0,
  subtitle: '',       // src of the chosen subtitle track, '' = off
  queue: null,        // music: [{path,title,artist,album,thumb}]
  index: 0,
  photos: null,       // photos in the same folder: [{path, name, file}]
  hideTimer: 0,
  reportTimer: 0,
  lastReport: 0,
  triedConvert: false,
  hls: false,         // converted as HLS: the browser seeks by itself (iPhone, iPad, Safari)
  quality: 0,         // 0 = original, or 1080 / 720 / 480 (converted to that size)
  repeat: 'off',      // music: off / all / one
  shuffled: false,
  lyrics: null,       // {synced, lines:[{t,text}], text}
  lyricIndex: -1,
};

// HLS is what Apple devices want for converted video: real seeking, no restarts.
const canHls = () => !!video().canPlayType('application/vnd.apple.mpegurl');
const video = () => $('video');

function isOpen() { return !$('player').hidden; }

function currentTime() { return P.converting && !P.hls ? P.offset + video().currentTime : video().currentTime; }
function duration() { return P.media && P.media.duration ? P.media.duration : (isFinite(video().duration) ? video().duration : 0); }

async function openMedia(path, opts = {}) {
  let res, m = null;
  try { res = await fetch('/api/media?path=' + encodeURIComponent(path)); } catch { res = null; }
  if (res && res.ok) m = await res.json();
  else {
    // The PC can't be reached (or refused): a copy saved for offline still plays.
    m = offlineMedia(path);
    if (!m) { if (res) alert(explainFailure(res)); else showMessage('Could not reach the LocalStream server.', true); return; }
  }
  if (!opts.keepQueue) P.queue = null;
  P.media = m;
  P.countedPlay = false;
  // A song opened on its own plays with the rest of its folder, so next / previous and the queue work.
  if (m.kind === 'audio' && !P.queue) await queueFolderOf(m);
  showPlayer();
  $('playerName').textContent = m.name;
  document.title = m.name + ' - LocalStream';
  $('resumePrompt').hidden = true;
  $('playerNotice').hidden = true;

  updateMarkButtons();
  onMediaOpened(m);
  $('btnPlayerWatched').hidden = m.kind !== 'video';
  $('btnPlayerPlaylist').hidden = m.kind === 'image' || (me && me.guest);
  $('btnPlayerFav').hidden = !!(me && me.guest);
  if (m.kind === 'image') { showPhoto(m); return; }

  $('photo').hidden = true; $('btnPrevPhoto').hidden = true; $('btnNextPhoto').hidden = true;
  video().hidden = false;
  $('playerBar').hidden = false;
  const isAudio = m.kind === 'audio';
  $('audioArt').hidden = !isAudio;
  $('player').classList.toggle('audio-mode', isAudio);
  if (isAudio) {
    const t = P.queue ? P.queue[P.index] : null;
    $('audioTitle').textContent = t ? t.title : m.name.replace(/\.[^.]+$/, '');
    $('audioSub').textContent = t ? [t.artist, t.album].filter(Boolean).join(' · ') : '';
    const cover = $('audioCover'); cover.hidden = false; cover.src = m.thumb;
    cover.onerror = () => { cover.hidden = true; };
  }
  $('btnPrevTrack').hidden = $('btnNextTrack').hidden = !P.queue || P.queue.length < 2;
  document.querySelectorAll('#player .music-only').forEach(b => { b.hidden = !isAudio; });
  $('btnQueue').hidden = !isAudio || !P.queue;
  $('btnShuffle').hidden = !isAudio || !P.queue || P.queue.length < 2;
  $('btnLyrics').hidden = !isAudio || !m.hasLyrics;
  $('queuePanel').hidden = !isAudio || $('queuePanel').hidden;
  renderQueue();
  loadLyrics(isAudio && m.hasLyrics ? m.path : null);
  const isVideo = m.kind === 'video';
  $('qualitySelect').hidden = !isVideo || !m.canConvert;
  $('qualitySelect').value = String(P.quality);
  $('btnFindSubs').hidden = !isVideo || !m.canFindSubtitles;

  // Tracks
  P.audioTrack = 0;
  const audioSel = $('audioSelect');
  audioSel.replaceChildren(...m.audio.map(a => el('option', { value: String(a.index), text: a.label })));
  audioSel.hidden = m.audio.length < 2 || !m.canConvert;
  const subsSel = $('subsSelect');
  subsSel.replaceChildren(el('option', { value: '', text: 'Subtitles off' }), ...m.subtitles.map(s => el('option', { value: s.src, text: s.label })));
  subsSel.hidden = m.subtitles.length === 0;
  $('btnSubStyle').hidden = m.subtitles.length === 0;
  P.subtitle = store.get('localStorage', 'subsOn', '1') === '1' && m.subtitles.length ? m.subtitles[0].src : '';
  subsSel.value = P.subtitle;

  P.triedConvert = false;
  const start = opts.start ?? 0;
  if (!opts.start && m.resume && m.resume > 10 && !isAudio) {
    // Ask, but start loading so the choice is instant.
    $('btnResume').textContent = `Resume from ${formatTime(m.resume)}`;
    $('resumePrompt').hidden = false;
    $('btnResume').onclick = () => { $('resumePrompt').hidden = true; seekTo(m.resume); playVideo(); };
    $('btnStartOver').onclick = () => { $('resumePrompt').hidden = true; seekTo(0); playVideo(); };
    loadSource(needsConvert(m), 0, false);
    return;
  }
  loadSource(needsConvert(m), start, true);
}

// Converted when the browser can't play the file, or when a smaller size was picked.
function needsConvert(m) {
  if (!m.canConvert) return false;
  if (P.quality && m.kind === 'video' && m.height > P.quality + 8) return true;
  return !m.direct;
}

function loadSource(convert, at, autoplay) {
  const v = video(), m = P.media;
  P.converting = convert;
  P.hls = convert && m.kind === 'video' && canHls() && m.duration > 0;
  $('convertBadge').hidden = !convert;
  $('convertBadge').textContent = convert && P.quality ? `Converting · ${P.quality}p` : 'Converting';
  const q = P.quality ? `&q=${P.quality}` : '';
  if (P.hls) {
    P.offset = 0;
    v.src = `${m.hls}?audio=${P.audioTrack}${q}`;
    if (at > 0) v.addEventListener('loadedmetadata', () => { v.currentTime = at; }, { once: true });
  } else if (convert) {
    P.offset = at;
    v.src = `${m.stream}?start=${at.toFixed(1)}&audio=${P.audioTrack}${q}`;
  } else {
    P.offset = 0;
    v.src = m.file;
    if (at > 0) v.addEventListener('loadedmetadata', () => { v.currentTime = at; }, { once: true });
  }
  setSubtitleTrack();
  $('playerSpinner').hidden = false;
  if (autoplay) playVideo();
  updateTime();
}

function playVideo() {
  const p = video().play();
  if (p) p.catch(() => { /* needs a tap first (autoplay rules) */ });
}

function setSubtitleTrack() {
  const v = video();
  v.querySelectorAll('track').forEach(t => t.remove());
  if (!P.subtitle) return;
  const shift = P.converting && !P.hls ? P.offset : 0;
  const track = el('track', { kind: 'subtitles', src: P.subtitle + (shift > 0 ? '&shift=' + shift.toFixed(1) : ''), default: true, label: 'Subtitles' });
  v.append(track);
  track.addEventListener('load', () => {
    if (!track.track) return;
    track.track.mode = 'showing';
    // A few lines up from the bottom, so the control bar never covers them.
    for (const cue of Array.from(track.track.cues || [])) { try { cue.snapToLines = true; cue.line = -4; } catch { } }
  });
  setTimeout(() => { if (track.track) track.track.mode = 'showing'; }, 50);
}

function seekTo(seconds) {
  const d = duration();
  seconds = Math.max(0, d ? Math.min(seconds, d - 1) : seconds);
  if (P.converting && !P.hls) {
    const playing = !video().paused;
    loadSource(true, seconds, playing || !$('resumePrompt').hidden);
  } else {
    video().currentTime = seconds;
  }
  updateTime();
}

function togglePlay() {
  if ($('player').hidden || !P.media || P.media.kind === 'image') return;
  $('resumePrompt').hidden = true;
  const v = video();
  if (v.paused) playVideo(); else v.pause();
}

function updateTime() {
  const d = duration(), t = currentTime();
  $('timeText').textContent = `${formatTime(t)} / ${formatTime(d)}`;
  if (!seeking) $('seek').value = d ? Math.round(t / d * 1000) : 0;
  setIcon($('btnPlay'), video().paused ? 'play-solid' : 'pause-solid');
}

let seeking = false;

function reportProgress(force) {
  if (!P.media || P.media.kind !== 'video') return;
  const now = Date.now();
  if (!force && now - P.lastReport < 10000) return;
  P.lastReport = now;
  const d = duration();
  if (d) postJson('/api/progress', { path: P.media.path, position: currentTime(), duration: d }, true).catch(() => {});
}

function showPlayer() {
  $('player').hidden = false;
  document.body.classList.add('player-open');
  pokeControls();
}

function closePlayer() {
  cancelNextUp();
  reportProgress(true);
  const v = video();
  v.pause();
  v.removeAttribute('src');
  v.querySelectorAll('track').forEach(t => t.remove());
  v.load();
  $('player').hidden = true;
  $('player').classList.remove('audio-mode');
  document.body.classList.remove('player-open');
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  document.title = state && !state.isRoot ? `${state.name} - LocalStream` : 'LocalStream';
  P.media = null;
  if (state && state.isRoot) loadHome();
}

function pokeControls() {
  const p = $('player');
  p.classList.remove('idle');
  clearTimeout(P.hideTimer);
  P.hideTimer = setTimeout(() => { if (!video().paused && P.media && P.media.kind === 'video') p.classList.add('idle'); }, 3000);
}

function toggleFullscreen() {
  if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  else ($('player').requestFullscreen || (() => Promise.reject())).call($('player')).catch(() => {
    // iPhone: only the video element itself can go full screen.
    const v = video(); if (v.webkitEnterFullscreen) v.webkitEnterFullscreen();
  });
}

// --- music queue

/** Makes the queue from the songs in the same folder as this one, starting at this song. */
async function queueFolderOf(m) {
  const folder = m.path.replace(/[\\/][^\\/]*$/, '');
  let songs = [];
  try {
    const res = await fetch('/api/list?path=' + encodeURIComponent(folder));
    const data = res.ok ? await res.json() : { items: [] };
    songs = data.items.filter(i => i.type === 'file' && fileIcon(i.name) === 'audio')
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
  } catch { }
  if (!P.media || P.media.path !== m.path) return; // another file was opened meanwhile
  const i = songs.findIndex(s => s.path === m.path);
  if (i < 0) { P.queue = [{ path: m.path, title: m.name.replace(/\.[^.]+$/, ''), artist: '', album: '', thumb: m.thumb }]; P.index = 0; return; }
  P.queue = songs.map(s => ({ path: s.path, title: s.name.replace(/\.[^.]+$/, ''), artist: '', album: baseName(folder), thumb: s.thumb }));
  P.index = i;
  P.shuffled = false; $('btnShuffle').classList.remove('on');
}

function playQueue(queue, index) {
  P.shuffled = false; $('btnShuffle').classList.remove('on');
  P.queue = queue.slice();
  P.index = index;
  openMedia(queue[index].path, { keepQueue: true });
}

function nextTrack(step, auto) {
  if (!P.queue) return;
  if (auto && P.repeat === 'one') { video().currentTime = 0; playVideo(); return; }
  let i = P.index + step;
  if (i >= P.queue.length) { if (P.repeat !== 'all') return; i = 0; }
  if (i < 0) i = P.repeat === 'all' ? P.queue.length - 1 : 0;
  // "Previous" a few seconds into a song starts it over, like music players do.
  if (step < 0 && !auto && video().currentTime > 4) { video().currentTime = 0; return; }
  P.index = i;
  openMedia(P.queue[i].path, { keepQueue: true });
}

function toggleShuffle() {
  if (!P.queue) return;
  const current = P.queue[P.index];
  if (!P.shuffled) {
    P.unshuffled = P.queue.slice();
    const rest = P.queue.filter((_, i) => i !== P.index);
    for (let i = rest.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [rest[i], rest[j]] = [rest[j], rest[i]]; }
    P.queue = [current, ...rest];
    P.index = 0;
  } else {
    P.queue = P.unshuffled || P.queue;
    P.index = Math.max(0, P.queue.indexOf(current));
  }
  P.shuffled = !P.shuffled;
  $('btnShuffle').classList.toggle('on', P.shuffled);
  renderQueue();
}

function cycleRepeat() {
  P.repeat = P.repeat === 'off' ? 'all' : P.repeat === 'all' ? 'one' : 'off';
  store.set('localStorage', 'repeat', P.repeat);
  showRepeat();
}

function showRepeat() {
  $('btnRepeat').classList.toggle('on', P.repeat !== 'off');
  $('repeatOne').hidden = P.repeat !== 'one';
  $('btnRepeat').title = 'Repeat: ' + ({ off: 'off', all: 'all songs', one: 'this song' })[P.repeat];
}

// The queue panel: tap to jump, move up and down, remove.
function renderQueue() {
  const list = $('queueList');
  if (!P.queue) { list.replaceChildren(); return; }
  list.replaceChildren(...P.queue.map((t, i) => {
    const art = el('div', { class: 'queue-thumb' }, icon('audio'));
    if (t.thumb) { const img = el('img', { src: t.thumb, loading: 'lazy', alt: '' }); img.addEventListener('error', () => img.remove()); art.append(img); }
    const row = el('div', { class: 'queue-row' + (i === P.index ? ' current' : ''), title: t.title },
      el('span', { class: 'track-no', text: String(i + 1) }), art,
      el('div', { class: 'name-wrap' }, el('span', { class: 'name', text: t.title }), el('span', { class: 'meta', text: t.artist || t.album || '' })),
      i > P.index + 1 ? el('button', { class: 'icon-btn dark small', title: 'Move up', type: 'button', onclick: (e) => { e.stopPropagation(); moveInQueue(i, -1); } }, icon('arrow-up')) : null,
      i > P.index && i < P.queue.length - 1 ? el('button', { class: 'icon-btn dark small', title: 'Move down', type: 'button', onclick: (e) => { e.stopPropagation(); moveInQueue(i, 1); } }, icon('arrow-down')) : null,
      i !== P.index ? el('button', { class: 'icon-btn dark small', title: 'Remove from queue', type: 'button', onclick: (e) => { e.stopPropagation(); removeFromQueue(i); } }, icon('x')) : null);
    row.addEventListener('click', () => { if (i !== P.index) { P.index = i; openMedia(P.queue[i].path, { keepQueue: true }); } });
    return row;
  }));
  const cur = list.querySelector('.current');
  if (cur && !$('queuePanel').hidden) cur.scrollIntoView({ block: 'nearest' });
}

function moveInQueue(i, step) {
  const j = i + step;
  if (j <= P.index || j >= P.queue.length) return;
  [P.queue[i], P.queue[j]] = [P.queue[j], P.queue[i]];
  renderQueue();
}

function removeFromQueue(i) {
  if (i === P.index) return;
  P.queue.splice(i, 1);
  if (i < P.index) P.index--;
  $('btnPrevTrack').hidden = $('btnNextTrack').hidden = P.queue.length < 2;
  renderQueue();
}

/** Adds songs after the current one ("Play next") or at the end. */
function enqueue(tracks, next) {
  if (!P.queue || !isOpen() || !P.media || P.media.kind !== 'audio') { playQueue(tracks, 0); return; }
  P.queue.splice(next ? P.index + 1 : P.queue.length, 0, ...tracks);
  $('btnPrevTrack').hidden = $('btnNextTrack').hidden = $('btnShuffle').hidden = false;
  $('btnQueue').hidden = false;
  renderQueue();
  notice(`${tracks.length === 1 ? '"' + tracks[0].title + '"' : tracks.length + ' songs'} added to the queue.`);
}

// --- lyrics (.lrc next to the song, or in its tags)

async function loadLyrics(path) {
  P.lyrics = null; P.lyricIndex = -1;
  const pane = $('lyrics');
  pane.replaceChildren();
  if (!path) { pane.hidden = true; return; }
  try {
    const res = await fetch('/api/lyrics?path=' + encodeURIComponent(path));
    const data = res.ok ? await res.json() : null;
    if (!data || !data.found || !P.media || P.media.path !== path) return;
    P.lyrics = data;
    pane.replaceChildren(...(data.synced ? data.lines.map(l => el('div', { class: 'lyric', text: l.text || '♪' }))
                                         : data.text.split('\n').map(t => el('div', { class: 'lyric plain', text: t }))));
    pane.hidden = store.get('localStorage', 'lyricsOn', '1') !== '1';
    $('btnLyrics').classList.toggle('on', !pane.hidden);
  } catch { }
}

function syncLyrics() {
  const L = P.lyrics;
  if (!L || !L.synced || $('lyrics').hidden) return;
  const t = video().currentTime + 0.2;
  let i = -1;
  for (let k = 0; k < L.lines.length && L.lines[k].t <= t; k++) i = k;
  if (i === P.lyricIndex) return;
  P.lyricIndex = i;
  const rows = $('lyrics').children;
  for (let k = 0; k < rows.length; k++) rows[k].classList.toggle('now', k === i);
  if (rows[i]) rows[i].scrollIntoView({ block: 'center', behavior: 'smooth' });
}

// --- watched / favorite buttons in the player

function updateMarkButtons() {
  const m = P.media;
  if (!m) return;
  const w = MY.watched.has(m.path), f = MY.favorites.has(m.path);
  $('btnPlayerWatched').classList.toggle('on', w);
  $('btnPlayerWatched').title = w ? 'Watched (click to unmark)' : 'Mark as watched';
  setIcon($('btnPlayerFav'), f ? 'heart-solid' : 'heart');
  $('btnPlayerFav').classList.toggle('on', f);
  $('btnPlayerFav').title = f ? 'In your favorites (click to remove)' : 'Add to favorites';
}

// --- photos

async function showPhoto(m) {
  const v = video(); v.pause(); v.removeAttribute('src');
  v.hidden = true; // otherwise the (empty) video still takes the full width and pushes the photo aside
  $('audioArt').hidden = true;
  $('playerBar').hidden = true;
  $('player').classList.remove('audio-mode');
  const img = $('photo');
  img.hidden = false;
  img.src = m.file;
  $('playerSpinner').hidden = true;
  // Siblings for next / previous (the folder being viewed, or the photo's own folder).
  const folder = m.path.replace(/[\\/][^\\/]*$/, '');
  const inList = P.photos && P.photos.list.some(p => p.path === m.path);
  if (!P.photos || (P.photos.folder !== folder && !(P.photos.timeline && inList))) {
    try {
      const res = await fetch('/api/list?path=' + encodeURIComponent(folder));
      const data = res.ok ? await res.json() : { items: [] };
      P.photos = { folder, list: data.items.filter(i => i.type === 'file' && fileIcon(i.name) === 'image') };
    } catch { P.photos = { folder, list: [] }; }
  }
  const i = P.photos.list.findIndex(p => p.path === m.path);
  $('btnPrevPhoto').hidden = i <= 0;
  $('btnNextPhoto').hidden = i < 0 || i >= P.photos.list.length - 1;
  $('playerName').textContent = m.name + (i >= 0 ? `  (${i + 1} of ${P.photos.list.length})` : '');
}

function nextPhoto(step) {
  if (!P.photos || !P.media || P.media.kind !== 'image') return;
  const i = P.photos.list.findIndex(p => p.path === P.media.path) + step;
  if (i >= 0 && i < P.photos.list.length) openMedia(P.photos.list[i].path);
}

function initPlayer() {
  const v = video();
  v.addEventListener('timeupdate', () => { updateTime(); reportProgress(false); });
  v.addEventListener('play', () => { updateTime(); pokeControls(); });
  v.addEventListener('pause', () => { updateTime(); reportProgress(true); $('player').classList.remove('idle'); });
  v.addEventListener('waiting', () => { $('playerSpinner').hidden = false; });
  v.addEventListener('playing', () => { $('playerSpinner').hidden = true; });
  v.addEventListener('canplay', () => { $('playerSpinner').hidden = true; });
  v.addEventListener('loadeddata', () => { $('playerSpinner').hidden = true; });
  v.addEventListener('ended', () => {
    reportProgress(true);
    if (P.media && P.media.kind === 'video') { MY.watched.add(P.media.path); updateMarkButtons(); }
    if (P.queue) nextTrack(1, true);
  });
  v.addEventListener('timeupdate', syncLyrics);
  // A song counts as played (for "Most played") after half of it, or 4 minutes.
  v.addEventListener('timeupdate', () => {
    const m = P.media;
    if (!m || m.kind !== 'audio' || P.countedPlay || !isFinite(v.duration) || v.duration <= 0) return;
    if (v.currentTime >= Math.min(240, v.duration * 0.5)) { P.countedPlay = true; postJson('/api/mark', { path: m.path, played: true }).catch(() => {}); }
  });
  v.addEventListener('error', () => {
    if (!P.media || !v.getAttribute('src')) return;
    $('playerSpinner').hidden = true;
    // The browser couldn't play the file as it is: let the PC convert it.
    if (!P.converting && P.media.canConvert && !P.triedConvert) {
      P.triedConvert = true;
      notice('This browser can\'t play this file directly. Converting it on the PC…');
      loadSource(true, v.currentTime || 0, true);
      return;
    }
    notice(P.converting ? 'The PC couldn\'t convert this file.' : 'This file can\'t be played in the browser. Download it, or use "Play on TV".');
  });

  $('btnPlay').addEventListener('click', togglePlay);
  $('btnPlayerClose').addEventListener('click', closePlayer);
  $('btnFullscreen').addEventListener('click', toggleFullscreen);
  $('btnPrevTrack').addEventListener('click', () => nextTrack(-1));
  $('btnNextTrack').addEventListener('click', () => nextTrack(1));
  $('btnPrevPhoto').addEventListener('click', () => nextPhoto(-1));
  $('btnNextPhoto').addEventListener('click', () => nextPhoto(1));
  $('playerStage').addEventListener('click', (e) => {
    if (e.target.closest('button') || !P.media || P.media.kind === 'image') return;
    togglePlay();
  });
  $('playerStage').addEventListener('dblclick', (e) => { if (!e.target.closest('button')) toggleFullscreen(); });
  $('player').addEventListener('pointermove', pokeControls);
  $('btnShuffle').addEventListener('click', toggleShuffle);
  $('btnRepeat').addEventListener('click', cycleRepeat);
  P.repeat = store.get('localStorage', 'repeat', 'off');
  showRepeat();
  $('btnQueue').addEventListener('click', () => { $('queuePanel').hidden = !$('queuePanel').hidden; renderQueue(); });
  $('btnQueueClose').addEventListener('click', () => { $('queuePanel').hidden = true; });
  $('btnLyrics').addEventListener('click', () => {
    const pane = $('lyrics');
    pane.hidden = !pane.hidden;
    store.set('localStorage', 'lyricsOn', pane.hidden ? '0' : '1');
    $('btnLyrics').classList.toggle('on', !pane.hidden);
    P.lyricIndex = -1; syncLyrics();
  });
  $('qualitySelect').addEventListener('change', () => {
    P.quality = +$('qualitySelect').value;
    store.set('localStorage', 'quality', String(P.quality));
    if (P.media) loadSource(needsConvert(P.media), currentTime(), !v.paused);
  });
  P.quality = +store.get('localStorage', 'quality', '0') || 0;
  $('btnPlayerWatched').addEventListener('click', () => P.media && markWatched(P.media.path, !MY.watched.has(P.media.path)).then(updateMarkButtons));
  $('btnPlayerFav').addEventListener('click', () => P.media && markFavorite(P.media.path, !MY.favorites.has(P.media.path)).then(updateMarkButtons));
  $('btnPlayerPlaylist').addEventListener('click', () => P.media && openPlaylistPicker([P.media.path], P.media.name));
  $('btnFindSubs').addEventListener('click', () => P.media && openSubtitleFinder(P.media));
  $('btnSubStyle').addEventListener('click', openSubStyle);
  $('btnPlayerTv').addEventListener('click', () => {
    if (!P.media) return;
    const at = P.media.kind === 'video' ? currentTime() : 0;
    video().pause();
    openTvPicker({ path: P.media.path, name: P.media.name, start: at, direct: P.media.direct, reason: P.media.reason });
  });

  const seek = $('seek');
  seek.addEventListener('input', () => { seeking = true; const d = duration(); $('timeText').textContent = `${formatTime(seek.value / 1000 * d)} / ${formatTime(d)}`; });
  seek.addEventListener('change', () => { seeking = false; seekTo(seek.value / 1000 * duration()); });

  const vol = $('volume');
  vol.value = store.get('localStorage', 'volume', '1');
  v.volume = +vol.value;
  vol.addEventListener('input', () => { v.volume = +vol.value; store.set('localStorage', 'volume', vol.value); });

  $('audioSelect').addEventListener('change', () => {
    // Browsers can't switch audio tracks of a file, so another track means converting from here.
    P.audioTrack = +$('audioSelect').value;
    loadSource(true, currentTime(), !v.paused);
  });
  $('subsSelect').addEventListener('change', () => {
    P.subtitle = $('subsSelect').value;
    store.set('localStorage', 'subsOn', P.subtitle ? '1' : '0');
    setSubtitleTrack();
  });

  document.addEventListener('keydown', (e) => {
    if (!isOpen() || e.target.matches('input[type=text], input[type=search], select')) return;
    const k = e.key;
    if (k === 'Escape' && document.querySelector('.modal:not([hidden])')) return; // a dialog over the player closes first
    if (k === 'Escape' && !document.fullscreenElement) { closePlayer(); e.preventDefault(); }
    else if (k === ' ' || k === 'k') { togglePlay(); e.preventDefault(); }
    else if (k === 'f') { toggleFullscreen(); }
    else if (k === 'n' && P.queue) { nextTrack(1); }
    else if (k === 'ArrowRight') { P.media && P.media.kind === 'image' ? nextPhoto(1) : seekTo(currentTime() + 10); e.preventDefault(); }
    else if (k === 'ArrowLeft') { P.media && P.media.kind === 'image' ? nextPhoto(-1) : seekTo(currentTime() - 10); e.preventDefault(); }
    else if (k === 'ArrowUp') { v.volume = Math.min(1, v.volume + 0.1); vol.value = v.volume; e.preventDefault(); }
    else if (k === 'ArrowDown') { v.volume = Math.max(0, v.volume - 0.1); vol.value = v.volume; e.preventDefault(); }
    pokeControls();
  });
  window.addEventListener('pagehide', () => reportProgress(true));

  // Phone lock screen / headset buttons for music.
  if ('mediaSession' in navigator) {
    const ms = navigator.mediaSession;
    ms.setActionHandler('play', () => playVideo());
    ms.setActionHandler('pause', () => v.pause());
    try { ms.setActionHandler('nexttrack', () => nextTrack(1)); ms.setActionHandler('previoustrack', () => nextTrack(-1)); } catch { }
    v.addEventListener('play', () => {
      if (!P.media) return;
      const t = P.queue ? P.queue[P.index] : null;
      try {
        ms.metadata = new MediaMetadata({ title: t ? t.title : P.media.name, artist: t ? t.artist : '', album: t ? t.album : '',
          artwork: [{ src: P.media.thumb, sizes: '512x512' }] });
      } catch { }
    });
  }
}

function notice(text) {
  const n = $('playerNotice');
  n.textContent = text;
  n.hidden = !text;
  clearTimeout(n._t);
  if (text) n._t = setTimeout(() => { n.hidden = true; }, 6000);
}

// ================================================================== Play on TV

const TV = { pending: null, device: store.get('localStorage', 'tvDevice', ''), timer: 0, status: null, seeking: false };

async function openTvPicker(item) {
  TV.pending = item;
  $('tvPickerTitle').textContent = `Play "${item.name}" on…`;
  $('tvPicker').hidden = false;
  const isVideo = fileIcon(item.name) === 'video' || fileIcon(item.name) === 'audio';
  $('tvConvertRow').hidden = !isVideo;
  $('tvQualityRow').hidden = fileIcon(item.name) !== 'video';
  $('tvQuality').value = '0';
  $('tvConvert').checked = false;
  $('tvConvertHint').textContent = item.reason ? `(this file has ${item.reason}; tick this if your TV can't play it)` : "(for TVs that can't play this file)";
  await scanTvs(false);
}

async function scanTvs(refresh) {
  const list = $('tvList');
  $('tvPickerStatus').textContent = 'Looking for TVs and speakers on your network…';
  list.replaceChildren();
  try {
    const res = await fetch('/api/tv/devices' + (refresh ? '?refresh=1' : ''));
    const data = await res.json();
    if (!data.canConvert) $('tvConvertRow').hidden = true;
    $('tvPickerStatus').textContent = data.devices.length ? '' : 'No TVs found. Make sure the TV is on and connected to the same network, then search again.';
    list.replaceChildren(...data.devices.map(d => el('button', { class: 'tv-device' + (d.id === TV.device ? ' last' : ''), type: 'button', onclick: () => playOnTv(d) },
      icon('tv'), el('span', {}, el('span', { class: 'name', text: d.name }), el('span', { class: 'meta', text: (d.cast ? 'Chromecast · ' : '') + (d.model || '').replace(/^Google Cast · /, '') })))));
  } catch {
    $('tvPickerStatus').textContent = 'Could not search for TVs.';
  }
}

async function playOnTv(device) {
  const item = TV.pending;
  $('tvPicker').hidden = true;
  if (!item) return;
  if (isOpen()) closePlayer();
  $('tvPickerStatus').textContent = '';
  const res = await postJson('/api/tv/play', { device: device.id, path: item.path, convert: $('tvConvert').checked, start: item.start || 0, quality: +$('tvQuality').value });
  if (!res.ok) {
    let msg = explainFailure(res);
    try { msg = (await res.json()).error || msg; } catch { }
    alert(msg);
    return;
  }
  TV.device = device.id;
  store.set('localStorage', 'tvDevice', device.id);
  pollTv();
}

async function pollTv() {
  clearTimeout(TV.timer);
  if (!TV.device) return;
  try {
    const res = await fetch('/api/tv/status?device=' + encodeURIComponent(TV.device));
    const data = res.ok ? await res.json() : null;
    TV.status = data && data.status;
  } catch { TV.status = null; }
  renderTvRemote();
  if (TV.status) TV.timer = setTimeout(pollTv, 2000);
}

function renderTvRemote() {
  const s = TV.status;
  $('tvRemote').hidden = !s;
  document.body.classList.toggle('tv-open', !!s);
  if (!s) return;
  const states = { PLAYING: 'Playing', PAUSED_PLAYBACK: 'Paused', TRANSITIONING: 'Loading…', STOPPED: 'Stopped', NO_MEDIA_PRESENT: 'Stopped' };
  $('tvTitle').textContent = s.title;
  $('tvState').textContent = `${states[s.state] || s.state} on ${s.deviceName}` + (s.duration ? ` · ${formatTime(s.position)} / ${formatTime(s.duration)}` : '') + (s.converted ? ' · converting' : '');
  setIcon($('btnTvPlay'), s.state === 'PLAYING' || s.state === 'TRANSITIONING' ? 'pause-solid' : 'play-solid');
  const seek = $('tvSeek');
  seek.hidden = !s.duration || s.isImage;
  $('btnTvBack').hidden = $('btnTvFwd').hidden = seek.hidden;
  if (!TV.seeking && s.duration) seek.value = Math.round(s.position / s.duration * 1000);
  const vol = $('tvVolume');
  vol.hidden = s.volume < 0;
  if (document.activeElement !== vol && s.volume >= 0) vol.value = s.volume;
}

async function tvControl(action, value) {
  if (!TV.device) return;
  try {
    const res = await postJson('/api/tv/control', { device: TV.device, action, value: value || 0 });
    const data = await res.json().catch(() => null);
    if (!res.ok && data && data.error) alert(data.error);
    if (data && 'status' in data) { TV.status = data.status; renderTvRemote(); }
  } catch { }
  if (action === 'stop') { TV.status = null; renderTvRemote(); } else pollTv();
}

function initTv() {
  $('btnTvCancel').addEventListener('click', () => { $('tvPicker').hidden = true; });
  $('btnTvRefresh').addEventListener('click', () => scanTvs(true));
  $('tvPicker').addEventListener('click', (e) => { if (e.target === $('tvPicker')) $('tvPicker').hidden = true; });
  $('btnTvPlay').addEventListener('click', () => tvControl(TV.status && (TV.status.state === 'PLAYING' || TV.status.state === 'TRANSITIONING') ? 'pause' : 'play'));
  $('btnTvStop').addEventListener('click', () => tvControl('stop'));
  $('btnTvBack').addEventListener('click', () => TV.status && tvControl('seek', Math.max(0, TV.status.position - 30)));
  $('btnTvFwd').addEventListener('click', () => TV.status && tvControl('seek', Math.min(TV.status.duration - 5, TV.status.position + 30)));
  const seek = $('tvSeek');
  seek.addEventListener('input', () => { TV.seeking = true; });
  seek.addEventListener('change', () => { TV.seeking = false; if (TV.status) tvControl('seek', seek.value / 1000 * TV.status.duration); });
  $('tvVolume').addEventListener('change', () => tvControl('volume', +$('tvVolume').value));
  if (TV.device) pollTv(); // something may still be playing from before a reload
}

// ================================================================== install as an app

let installPrompt = null;

function initInstall() {
  // Browsers only allow full installs (and offline start) on https or localhost.
  if ('serviceWorker' in navigator && window.isSecureContext) navigator.serviceWorker.register('/sw.js').catch(() => {});
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault();
    installPrompt = e;
    $('btnInstall').hidden = false;
  });
  $('btnInstall').addEventListener('click', async () => {
    if (installPrompt) { installPrompt.prompt(); installPrompt = null; $('btnInstall').hidden = true; }
  });
  window.addEventListener('appinstalled', () => { $('btnInstall').hidden = true; });
}

// ------------------------------------------------------------------ start

async function loadPage() {
  await loadMe();
  const views = { offline: loadOffline, music: loadMusic, movies: loadMovies, shows: () => loadShows('tv'), webseries: () => loadShows('web'), photos: loadPhotos, favorites: loadFavorites, playlists: loadPlaylists };
  if (views[view]) {
    markNav(view);
    try { await views[view](); } catch (e) { console.error(e); showMessage('Could not reach the LocalStream server.', true); }
    return;
  }
  markNav('');
  try {
    const res = await fetch('/api/list' + (currentPath ? '?path=' + encodeURIComponent(currentPath) : ''));
    if (res.status === 404) {
      showMessage('This folder does not exist or is not shared.', true);
      state = { isRoot: true, role: 'Viewer', name: 'Not found', crumbs: [], items: [] };
      renderBreadcrumbs(); renderTopBar();
      return;
    }
    if (!res.ok) { showMessage(explainFailure(res), true); return; }
    state = await res.json();
    render();
    watchForChanges();
    const play = query.get('play');
    if (play) openMedia(play);
  } catch (e) {
    showMessage('Could not reach the LocalStream server.', true);
  }
}

document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('[data-icon]').forEach(s => { s.innerHTML = ICONS[s.getAttribute('data-icon')] || ''; });
  applyViewSettings();

  $('container').addEventListener('click', onItemClick);
  $('searchList').addEventListener('click', onItemClick);
  $('searchInput').addEventListener('input', onSearchInput);
  $('sortSelect').addEventListener('change', sortItems);
  $('gridSizeSelect').addEventListener('change', () => {
    store.set('localStorage', 'gridSize', $('gridSizeSelect').value);
    applyViewSettings();
  });
  $('btnTheme').addEventListener('click', () => {
    const next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    store.set('localStorage', 'theme', next);
    applyViewSettings();
  });
  $('btnView').addEventListener('click', () => {
    const c = $('container');
    if (toggleLayout(c)) return;
    const next = c.classList.contains('grid') ? 'list' : 'grid';
    store.set('localStorage', 'viewMode', next);
    applyViewSettings();
  });
  $('btnSelectMode').addEventListener('click', toggleSelectMode);
  $('btnPaste').addEventListener('click', pasteItems);
  $('btnNewFolder').addEventListener('click', createFolder);
  $('btnUpload').addEventListener('click', () => $('fileInput').click());
  $('fileInput').addEventListener('change', (e) => uploadFiles(Array.from(e.target.files)));
  $('btnDownloadEach').addEventListener('click', downloadEach);
  $('btnZipSelected').addEventListener('click', downloadSelectedAsZip);
  $('btnCopySelected').addEventListener('click', () => setClipboard('copy', selectedChecks().map(c => c.dataset.fullpath)));
  $('btnCutSelected').addEventListener('click', () => setClipboard('move', selectedChecks().map(c => c.dataset.fullpath)));
  $('btnDeleteSelected').addEventListener('click', () => deletePaths(selectedChecks().map(c => c.dataset.fullpath)));

  initPlayer();
  initLibrary();
  initTv();
  initInstall();
  loadPage();
});
