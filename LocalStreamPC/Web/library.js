// LocalStream web UI, part 2: the library pages (Movies, TV shows, Photos, Favorites, Playlists),
// the account menu, share links, "Add to playlist", "Find subtitles" and subtitle style.
// Uses the helpers in app.js (el, icon, $, store, openMedia...). Built with DOM APIs only.
'use strict';

const MY = { watched: new Set(), favorites: new Set(), playlists: [] };

async function loadMe() {
  try {
    const [meRes, libRes] = await Promise.all([fetch('/api/me'), fetch('/api/mylib')]);
    if (meRes.ok) me = await meRes.json();
    if (libRes.ok) {
      const lib = await libRes.json();
      MY.watched = new Set(lib.watched);
      MY.favorites = new Set(lib.favorites.map(f => f.path));
      MY.favoriteItems = lib.favorites;
      MY.playlists = lib.playlists;
    }
  } catch { /* offline: the page shows its own message */ }
  renderUserMenu();
}

function renderUserMenu() {
  const guest = me && me.guest;
  $('userMenu').hidden = !me || !me.authEnabled || (!me.user && !guest);
  if (me) {
    $('userName').textContent = guest ? 'Guest' : me.user || '';
    $('userRole').textContent = guest ? 'Opened from a share link' : `Signed in as ${me.user}${me.role === 'Admin' ? ' (admin)' : ''}`;
    $('btnMyLinks').hidden = !me.canShare;
    $('btnLogout').lastChild.textContent = guest ? ' Leave' : ' Sign out';
  }
  // Share-link visitors only see what was shared.
  $('navTabs').hidden = !!guest;
  document.body.classList.toggle('guest', !!guest);
}

function markNav(current) {
  document.querySelectorAll('#navTabs .tab').forEach(t => t.classList.toggle('active', t.dataset.view === current));
}

/** Shows the Movies / TV shows / Music / Photos tabs only when there is something in them. */
async function updateNavTabs() {
  try {
    const res = await fetch('/api/home');
    if (!res.ok) return;
    const home = await res.json();
    const tab = (v) => document.querySelector(`#navTabs .tab[data-view="${v}"]`);
    tab('music').hidden = !home.hasMusic && view !== 'music';
    tab('movies').hidden = !home.hasMovies && view !== 'movies';
    tab('shows').hidden = !home.hasShows && view !== 'shows';
    tab('webseries').hidden = !home.hasWebSeries && view !== 'webseries';
    tab('photos').hidden = !home.hasPhotos && view !== 'photos';
    tab('offline').hidden = !(offlineSupported() && offlineIndex().length) && view !== 'offline';
  } catch { }
}

// ------------------------------------------------------------------ new files appear by themselves

let libVersion = null, versionTimer = 0;

function watchForChanges() {
  clearTimeout(versionTimer);
  versionTimer = setTimeout(async () => {
    try {
      if (document.visibilityState === 'visible' && !isOpen() && !document.body.classList.contains('select-mode')) {
        const res = await fetch('/api/version');
        if (res.ok) {
          const { v } = await res.json();
          if (libVersion !== null && v !== libVersion) await refreshListing();
          libVersion = v;
        }
      }
    } catch { }
    watchForChanges();
  }, libVersion === null ? 1500 : 8000);
}

async function refreshListing() {
  const res = await fetch('/api/list' + (currentPath ? '?path=' + encodeURIComponent(currentPath) : ''));
  if (!res.ok) return;
  const scroll = window.scrollY;
  state = await res.json();
  showMessage('');
  render();
  window.scrollTo(0, scroll);
}

// ------------------------------------------------------------------ marks

async function markWatched(path, watched) {
  const res = await postJson('/api/mark', { path, watched });
  if (res.ok) watched ? MY.watched.add(path) : MY.watched.delete(path);
  document.querySelectorAll('.item[data-path]').forEach(i => { if (i.dataset.path === path) i.classList.toggle('watched', watched); });
  return res.ok;
}

async function markFavorite(path, favorite) {
  const res = await postJson('/api/mark', { path, favorite });
  if (res.ok) favorite ? MY.favorites.add(path) : MY.favorites.delete(path);
  return res.ok;
}

// ------------------------------------------------------------------ page helpers

function pageHeader(iconName, title, crumbs) {
  const nav = $('breadcrumbs');
  nav.replaceChildren(el('a', { href: '/' }, icon('server'), document.createTextNode(' Home')));
  for (const [text, href] of crumbs) {
    nav.append(el('span', { class: 'sep', text: '/' }), href ? el('a', { href, text }) : el('span', { class: 'crumb-text', text }));
  }
  $('title').replaceChildren(icon(iconName), document.createTextNode(' ' + title));
  document.title = title + ' - LocalStream';
  $('btnSelectMode').hidden = true; $('sortSelect').hidden = true; $('adminControls').hidden = true;
  $('searchInput').placeholder = 'Search all folders...';
  state = { isRoot: false, role: me ? me.role : 'Viewer', crumbs: [], items: [], canShare: me && me.canShare };
  // Grid size and grid/list don't apply to poster and photo pages.
  const own = ['movies', 'shows', 'webseries', 'photos'].includes(view);
  document.querySelector('.grid-select-wrapper').hidden = own;
  $('btnView').hidden = own;
  updateNavTabs();
}

function posterCard({ title, sub, poster, thumb, progress, watched, onclick, badge, overview }) {
  const c = el('div', { class: 'poster-card', tabindex: '0', title });
  const art = el('div', { class: 'poster' }, icon('film'));
  const src = poster || thumb;
  if (src) {
    const img = el('img', { src, loading: 'lazy', alt: '', class: poster ? '' : 'from-video' });
    img.addEventListener('error', () => img.remove());
    art.append(img);
  }
  if (progress > 0 && progress < 1) {
    const bar = el('div', { class: 'progress' }, el('div'));
    bar.firstChild.style.width = Math.max(3, progress * 100) + '%';
    art.append(bar);
  }
  if (watched) art.append(el('span', { class: 'watched-badge', title: 'Watched' }, icon('check')));
  if (badge) art.append(el('span', { class: 'count-badge', text: badge }));
  c.append(art, el('div', { class: 'card-text' }, el('div', { class: 'name', text: title }), el('div', { class: 'meta', text: sub || '' }),
    overview ? el('div', { class: 'overview', text: overview }) : null));
  c.addEventListener('click', onclick);
  c.addEventListener('keydown', (e) => { if (e.key === 'Enter') onclick(); });
  return c;
}

/** While TMDB lookups are still running, look again shortly so posters fill in. */
function retryWhilePending(data, again) {
  if (data.error) showMessage('TMDB: ' + data.error + ' Posters and descriptions will appear once it works.', true);
  else if (data.pending > 0) { showMessage(`Finding posters and descriptions… (${data.pending} to go)`); setTimeout(again, 2500); }
  else showMessage('');
}

// ------------------------------------------------------------------ movies

async function loadMovies() {
  pageHeader('film', 'Movies', [['Movies', null]]);
  const res = await fetch('/api/videos?view=movies');
  if (!res.ok) { showMessage(explainFailure(res), true); return; }
  const data = await res.json();
  const c = $('container');
  fixLayout(c, 'posters');
  if (!data.movies.length) { showMessage('No movies found. Episodes named like "Show S01E02" are under TV shows.'); c.replaceChildren(); return; }
  if (!data.tmdb) showMessage('Tip: add a free TMDB key in the LocalStream app (Settings → Library) to get posters and descriptions.');
  const sort = store.get('localStorage', 'movieSort', 'title');
  const list = data.movies.slice().sort((a, b) => sort === 'added' ? new Date(b.added) - new Date(a.added) : sort === 'year' ? (b.year - a.year) : 0);
  const sortSel = el('select', { class: 'select-dropdown', title: 'Sort movies' },
    el('option', { value: 'title', text: 'A–Z' }), el('option', { value: 'added', text: 'Recently added' }), el('option', { value: 'year', text: 'Newest first' }));
  sortSel.value = sort;
  sortSel.addEventListener('change', () => { store.set('localStorage', 'movieSort', sortSel.value); loadMovies(); });
  const unwatched = el('label', { class: 'check inline' }, el('input', { type: 'checkbox', id: 'hideWatched' }), document.createTextNode(' Hide watched'));
  const tools = el('div', { class: 'page-tools' }, sortSel, unwatched);
  document.querySelectorAll('.page-tools').forEach(t => t.remove());
  c.before(tools);
  const hide = $('hideWatched');
  hide.checked = store.get('localStorage', 'hideWatched', '0') === '1';
  const draw = () => {
    c.replaceChildren(...list.filter(m => !(hide.checked && MY.watched.has(m.item.path))).map(m => posterCard({
      title: m.title, sub: [m.year || '', m.rating ? '★ ' + m.rating : ''].filter(Boolean).join(' · '),
      poster: m.poster, thumb: m.item.thumb, progress: m.progress, watched: MY.watched.has(m.item.path), overview: m.overview,
      onclick: () => showMovieInfo(m),
    })));
  };
  hide.addEventListener('change', () => { store.set('localStorage', 'hideWatched', hide.checked ? '1' : '0'); draw(); });
  draw();
  retryWhilePending(data, loadMovies);
}

function showMovieInfo(m) {
  const poster = $('infoPoster');
  poster.replaceChildren(icon('film'));
  if (m.poster || m.item.thumb) { const img = el('img', { src: m.poster || m.item.thumb, alt: '' }); img.addEventListener('error', () => img.remove()); poster.append(img); }
  $('infoTitle').textContent = m.title;
  $('infoMeta').textContent = [m.year || '', m.rating ? '★ ' + m.rating + ' on TMDB' : '', formatSize(m.item.size), m.item.name].filter(Boolean).join(' · ');
  $('infoOverview').textContent = m.overview || '';
  const path = m.item.path;
  const actions = $('infoActions');
  const watchedBtn = el('button', { class: 'btn btn-secondary', type: 'button' });
  const favBtn = el('button', { class: 'btn btn-secondary', type: 'button' });
  const paint = () => {
    watchedBtn.replaceChildren(icon('check'), document.createTextNode(MY.watched.has(path) ? ' Watched' : ' Mark watched'));
    watchedBtn.classList.toggle('on', MY.watched.has(path));
    favBtn.replaceChildren(icon(MY.favorites.has(path) ? 'heart-solid' : 'heart'), document.createTextNode(MY.favorites.has(path) ? ' Favorite' : ' Add to favorites'));
  };
  watchedBtn.addEventListener('click', async () => { await markWatched(path, !MY.watched.has(path)); paint(); });
  favBtn.addEventListener('click', async () => { await markFavorite(path, !MY.favorites.has(path)); paint(); });
  paint();
  const play = el('button', { class: 'btn btn-primary', type: 'button', onclick: () => { $('infoModal').hidden = true; openMedia(path); } },
    icon('play-solid'), document.createTextNode(m.progress > 0 && m.progress < 1 ? ' Resume' : ' Play'));
  actions.replaceChildren(play, watchedBtn, favBtn,
    el('button', { class: 'btn btn-secondary', type: 'button', onclick: () => openPlaylistPicker([path], m.title) }, icon('list-plus'), document.createTextNode(' Playlist')),
    el('button', { class: 'btn btn-secondary', type: 'button', onclick: () => { $('infoModal').hidden = true; openTvPicker({ path, name: m.item.name }); } }, icon('tv'), document.createTextNode(' TV')),
    me && me.canShare ? el('button', { class: 'btn btn-secondary', type: 'button', onclick: () => openShare(path) }, icon('share'), document.createTextNode(' Share')) : null);
  $('infoModal').hidden = false;
}

// ------------------------------------------------------------------ TV shows

// TV shows and web series share these pages; kind is 'tv' or 'web'.
const SHOW_KINDS = { tv: { label: 'TV shows', view: 'shows' }, web: { label: 'Web series', view: 'webseries' } };

async function loadShows(kind = 'tv') {
  const K = SHOW_KINDS[kind];
  const key = query.get('show');
  if (key) return loadShow(key, kind);
  pageHeader('tv', K.label, [[K.label, null]]);
  const res = await fetch('/api/videos?view=shows&kind=' + kind);
  if (!res.ok) { showMessage(explainFailure(res), true); return; }
  const data = await res.json();
  const c = $('container');
  fixLayout(c, 'posters');
  if (!data.shows.length) {
    showMessage(kind === 'web'
      ? 'No web series found. Put them in a folder called "Web Series" (like "Web Series/Mirzapur/Season 1/"), with episodes named like "Mirzapur S01E02.mkv" or "Episode 2.mkv".'
      : 'No TV shows found. Name episodes like "Show Name S01E02.mkv", or put them in "Show/Season 1/" folders.');
    return;
  }
  c.replaceChildren(...data.shows.map(s => posterCard({
    title: s.title, sub: `${s.seasons} season${s.seasons === 1 ? '' : 's'} · ${s.episodes} episodes`,
    poster: s.poster, thumb: s.thumb, overview: s.overview, badge: s.unwatched > 0 ? String(s.unwatched) : null, watched: s.unwatched === 0,
    onclick: () => { location.href = `/?view=${K.view}&show=` + encodeURIComponent(s.key); },
  })));
  retryWhilePending(data, () => loadShows(kind));
}

async function loadShow(key, kind = 'tv') {
  const K = SHOW_KINDS[kind];
  const res = await fetch('/api/videos?show=' + encodeURIComponent(key));
  if (!res.ok) { pageHeader('tv', K.label, [[K.label, '/?view=' + K.view]]); showMessage(res.status === 404 ? 'This show is no longer in your folders.' : explainFailure(res), true); return; }
  const d = await res.json();
  pageHeader('tv', d.title, [[K.label, '/?view=' + K.view], [d.title, null]]);
  const c = $('container');
  fixLayout(c, 'episodes');
  document.querySelectorAll('.show-head, .season-tabs').forEach(n => n.remove());
  const head = el('div', { class: 'show-head' });
  const art = el('div', { class: 'show-poster' }, icon('tv'));
  if (d.poster) { const img = el('img', { src: d.poster, alt: '' }); img.addEventListener('error', () => img.remove()); art.append(img); }
  const nextEp = d.next && d.seasons.flatMap(s => s.episodes.map(e => ({ s: s.number, e }))).find(x => x.e.item.path === d.next);
  head.append(art, el('div', { class: 'show-text' },
    el('h2', { text: d.title }),
    el('div', { class: 'meta', text: [d.year || '', d.rating ? '★ ' + d.rating : '', `${d.seasons.length} season${d.seasons.length === 1 ? '' : 's'}`].filter(Boolean).join(' · ') }),
    el('p', { text: d.overview || '' }),
    nextEp ? el('button', { class: 'btn btn-primary', type: 'button', onclick: () => playEpisodes(d, nextEp.e.item.path) }, icon('play-solid'),
      document.createTextNode(` ${nextEp.e.progress > 0 ? 'Resume' : 'Play'} S${nextEp.s} E${nextEp.e.number}`)) : el('span', { class: 'meta', text: 'All watched.' })));
  c.before(head);
  let season = +(store.get('sessionStorage', 'season:' + key, '') || (nextEp ? nextEp.s : d.seasons[0].number));
  if (!d.seasons.some(s => s.number === season)) season = d.seasons[0].number;
  const tabs = el('div', { class: 'season-tabs music-tabs' });
  const drawSeason = () => {
    tabs.replaceChildren(...d.seasons.map(s => el('button', { class: 'tab' + (s.number === season ? ' active' : ''), type: 'button', text: s.number === 0 ? 'Specials' : `Season ${s.number}`,
      onclick: () => { season = s.number; store.set('sessionStorage', 'season:' + key, String(season)); drawSeason(); } })));
    const s = d.seasons.find(x => x.number === season);
    c.replaceChildren(...s.episodes.map(e => episodeRow(d, e)));
  };
  c.before(tabs);
  drawSeason();
  retryWhilePending(d, () => loadShow(key, kind));
}

function episodeRow(show, e) {
  const row = el('div', { class: 'item episode' + (MY.watched.has(e.item.path) ? ' watched' : '') });
  row.dataset.path = e.item.path;
  const art = el('div', { class: 'episode-still' }, icon('video'));
  const src = e.still || e.item.thumb;
  if (src) { const img = el('img', { src, loading: 'lazy', alt: '' }); img.addEventListener('error', () => img.remove()); art.append(img); }
  if (e.progress > 0 && e.progress < 1) { const bar = el('div', { class: 'progress' }, el('div')); bar.firstChild.style.width = Math.max(3, e.progress * 100) + '%'; art.append(bar); }
  const watchBtn = actionButton('check', 'Watched / not watched', async (ev) => {
    ev.stopPropagation();
    const now = !MY.watched.has(e.item.path);
    await markWatched(e.item.path, now);
  });
  row.append(art,
    el('div', { class: 'details' },
      el('div', { class: 'name-wrap' },
        el('span', { class: 'name', text: `${e.number}. ${e.title}` }),
        el('span', { class: 'meta ep-overview', text: e.overview || e.item.name })),
      el('div', { class: 'actions' }, watchBtn,
        actionButton('tv', 'Play on TV', (ev) => { ev.stopPropagation(); openTvPicker({ path: e.item.path, name: e.item.name }); }),
        actionButton('list-plus', 'Add to a playlist', (ev) => { ev.stopPropagation(); openPlaylistPicker([e.item.path], e.title); }))));
  row.addEventListener('click', () => playEpisodes(show, e.item.path));
  return row;
}

/** Plays an episode; when it ends the next one starts (like a streaming app). */
function playEpisodes(show, path) {
  const all = show.seasons.flatMap(s => s.episodes.map(e => ({ path: e.item.path, title: `S${s.number} E${e.number} · ${e.title}`, artist: show.title })));
  P.episodes = all;
  openMedia(path);
}

// ------------------------------------------------------------------ photos by date

async function loadPhotos() {
  pageHeader('image', 'Photos', [['Photos', null]]);
  const res = await fetch('/api/photos');
  if (!res.ok) { showMessage(explainFailure(res), true); return; }
  const data = await res.json();
  const c = $('container');
  fixLayout(c, 'timeline');
  if (!data.total) { showMessage('No photos found in your shared folders.'); return; }
  if (!data.complete) showMessage('Still reading the dates of your photos. Reload in a moment for the full timeline.');
  const all = data.months.flatMap(m => m.items);
  const list = all.map(p => ({ path: p.path, name: p.name, file: p.file }));
  c.replaceChildren(...data.months.map(m => el('section', { class: 'month' },
    el('h3', {}, document.createTextNode(m.label + ' '), el('span', { class: 'meta', text: `${m.items.length} photo${m.items.length === 1 ? '' : 's'}` })),
    el('div', { class: 'photo-grid' }, ...m.items.map(p => {
      const tile = el('button', { class: 'photo-tile', type: 'button', title: `${p.name} · ${new Date(p.taken).toLocaleString()}` });
      const img = el('img', { src: p.thumb, loading: 'lazy', alt: p.name });
      img.addEventListener('error', () => { img.remove(); tile.append(icon('image')); });
      tile.append(img, el('span', { class: 'photo-caption' }, el('span', { class: 'name', text: p.name }), el('span', { class: 'meta', text: new Date(p.taken).toLocaleString() })));
      tile.addEventListener('click', () => { P.photos = { folder: '', timeline: true, list }; openMedia(p.path); });
      return tile;
    })))));
}

// ------------------------------------------------------------------ favorites

async function loadFavorites() {
  pageHeader('heart', 'Favorites', [['Favorites', null]]);
  const items = MY.favoriteItems || [];
  const c = $('container');
  applyViewSettings();
  if (!items.length) { showMessage('Nothing here yet. Tap the heart in the player to add favorites.'); c.replaceChildren(); return; }
  state.items = items;
  c.replaceChildren(...items.map(i => {
    const node = renderFile(i, false);
    node.querySelectorAll('.file-check').forEach(x => x.remove());
    node.querySelector('.actions').append(actionButton('heart-solid', 'Remove from favorites', async (e) => {
      e.stopPropagation();
      if (await markFavorite(i.path, false)) node.remove();
    }, true));
    return node;
  }));
}

// ------------------------------------------------------------------ playlists

async function loadPlaylists() {
  const id = query.get('id');
  if (id) return loadPlaylist(id);
  pageHeader('list', 'Playlists', [['Playlists', null]]);
  const c = $('container');
  c.className = 'container grid';
  const create = el('div', { class: 'inline-form page-tools' },
    el('input', { type: 'text', id: 'plName', placeholder: 'New playlist name', maxlength: '80' }),
    el('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
      const name = $('plName').value.trim();
      const res = await postJson('/api/playlist', { action: 'create', name: name || 'New playlist' });
      if (res.ok) location.href = '/?view=playlists&id=' + (await res.json()).id;
      else alert((await res.json().catch(() => ({}))).error || explainFailure(res));
    } }, icon('list-plus'), document.createTextNode(' Create')));
  document.querySelectorAll('.page-tools').forEach(t => t.remove());
  c.before(create);
  if (!MY.playlists.length) { showMessage('No playlists yet. Make one here, or use "Add to playlist" in the player.'); c.replaceChildren(); return; }
  c.replaceChildren(...MY.playlists.map(p => musicCard(p.name, `${p.count} item${p.count === 1 ? '' : 's'}`, p.cover, 'list', '/?view=playlists&id=' + encodeURIComponent(p.id))));
}

async function loadPlaylist(id) {
  const res = await fetch('/api/playlist?id=' + encodeURIComponent(id));
  if (!res.ok) { pageHeader('list', 'Playlists', [['Playlists', '/?view=playlists']]); showMessage('This playlist no longer exists.', true); return; }
  const pl = await res.json();
  pageHeader('list', pl.name, [['Playlists', '/?view=playlists'], [pl.name, null]]);
  const c = $('container');
  fixLayout(c, 'playlist');
  const queue = () => pl.items.map(t => ({ path: t.item.path, title: t.title, artist: t.artist || '', album: t.album || '', thumb: t.item.thumb }));
  const save = () => postJson('/api/playlist', { action: 'set', id, paths: pl.items.map(t => t.item.path) });
  document.querySelectorAll('.album-head').forEach(n => n.remove());
  const head = el('div', { class: 'album-head' },
    el('div', { class: 'album-cover' }, icon('list')),
    el('div', {}, el('h2', { text: pl.name }), el('div', { class: 'meta', text: `${pl.items.length} item${pl.items.length === 1 ? '' : 's'}` }),
      el('div', { class: 'info-actions' },
        el('button', { class: 'btn btn-primary', type: 'button', disabled: !pl.items.length, onclick: () => playQueue(queue(), 0) }, icon('play-solid'), document.createTextNode(' Play')),
        el('button', { class: 'btn btn-secondary', type: 'button', disabled: !pl.items.length, onclick: () => { playQueue(queue(), 0); setTimeout(toggleShuffle, 0); } }, icon('shuffle'), document.createTextNode(' Shuffle')),
        el('button', { class: 'btn btn-secondary', type: 'button', onclick: async () => {
          const name = prompt('Rename playlist:', pl.name);
          if (name && name.trim() && (await postJson('/api/playlist', { action: 'rename', id, name: name.trim() })).ok) location.reload();
        } }, icon('edit'), document.createTextNode(' Rename')),
        el('button', { class: 'btn btn-danger', type: 'button', onclick: async () => {
          if (confirm(`Delete the playlist "${pl.name}"? The files themselves stay.`) && (await postJson('/api/playlist', { action: 'delete', id })).ok) location.href = '/?view=playlists';
        } }, icon('trash'), document.createTextNode(' Delete')))));
  if (pl.items[0]) { const img = el('img', { src: pl.items[0].item.thumb, alt: '' }); img.addEventListener('error', () => img.remove()); head.firstChild.append(img); }
  c.before(head);
  const draw = () => c.replaceChildren(...pl.items.map((t, i) => {
    const row = el('div', { class: 'item track', title: t.title });
    const cover = el('div', { class: 'queue-thumb light song-cover' }, icon(t.item.kind === 'video' ? 'video' : 'audio'));
    if (t.item.thumb) { const img = el('img', { src: t.item.thumb, loading: 'lazy', alt: '' }); img.addEventListener('error', () => img.remove()); cover.append(img); }
    row.append(el('span', { class: 'track-no', text: String(i + 1) }), cover,
      el('div', { class: 'details' },
        el('div', { class: 'name-wrap' }, el('span', { class: 'name', text: t.title }), el('span', { class: 'meta', text: t.artist || baseName(t.item.folder || '') })),
        el('div', { class: 'actions' },
          i > 0 ? actionButton('arrow-up', 'Move up', async (e) => { e.stopPropagation(); [pl.items[i - 1], pl.items[i]] = [pl.items[i], pl.items[i - 1]]; draw(); await save(); }) : null,
          i < pl.items.length - 1 ? actionButton('arrow-down', 'Move down', async (e) => { e.stopPropagation(); [pl.items[i + 1], pl.items[i]] = [pl.items[i], pl.items[i + 1]]; draw(); await save(); }) : null,
          actionButton('x', 'Remove from playlist', async (e) => { e.stopPropagation(); pl.items.splice(i, 1); draw(); await postJson('/api/playlist', { action: 'remove', id, paths: [t.item.path] }); }, true))));
    row.addEventListener('click', (e) => {
      if (e.target.closest('.btn-action')) return;
      if (t.item.kind === 'audio') playQueue(queue(), i); else openMedia(t.item.path);
    });
    return row;
  }));
  draw();
  if (!pl.items.length) showMessage('This playlist is empty. Use "Add to playlist" in the player or on a movie.');
}

// --- "Add to playlist" picker

let pickerPaths = [];

function openPlaylistPicker(paths, label) {
  pickerPaths = paths;
  $('playlistModalTitle').textContent = `Add "${label}" to…`;
  $('newPlaylistName').value = '';
  const list = $('playlistChoices');
  list.replaceChildren(...MY.playlists.map(p => el('button', { class: 'tv-device', type: 'button', onclick: () => addToPlaylist(p.id, p.name) },
    icon('list'), el('span', {}, el('span', { class: 'name', text: p.name }), el('span', { class: 'meta', text: `${p.count} item${p.count === 1 ? '' : 's'}` })))));
  if (!MY.playlists.length) list.append(el('p', { class: 'meta', text: 'No playlists yet: make the first one below.' }));
  $('playlistModal').hidden = false;
}

async function addToPlaylist(id, name) {
  const res = await postJson('/api/playlist', { action: 'add', id, paths: pickerPaths });
  $('playlistModal').hidden = true;
  if (!res.ok) { alert(explainFailure(res)); return; }
  const p = MY.playlists.find(x => x.id === id);
  if (p) p.count += pickerPaths.length;
  if (isOpen()) notice(`Added to "${name}".`); else showMessage(`Added to "${name}".`);
}

async function createPlaylistFromPicker() {
  const name = $('newPlaylistName').value.trim() || 'New playlist';
  const res = await postJson('/api/playlist', { action: 'create', name, paths: pickerPaths });
  $('playlistModal').hidden = true;
  if (!res.ok) { alert((await res.json().catch(() => ({}))).error || explainFailure(res)); return; }
  const { id } = await res.json();
  MY.playlists.unshift({ id, name, count: pickerPaths.length });
  if (isOpen()) notice(`Made the playlist "${name}".`); else showMessage(`Made the playlist "${name}".`);
}

// ------------------------------------------------------------------ share links

let sharePath = '';

async function openShare(path) {
  sharePath = path;
  $('shareTitle').textContent = `Share "${baseName(path)}"`;
  $('shareResult').hidden = true;
  $('shareNote').textContent = '';
  $('btnShareCreate').hidden = false;
  $('shareModal').hidden = false;
  loadShareList(path);
}

async function loadShareList(path) {
  const box = $('shareList');
  box.replaceChildren();
  try {
    const res = await fetch('/api/shares');
    if (!res.ok) return;
    const { shares } = await res.json();
    const mine = path ? shares.filter(s => s.path === path) : shares;
    if (!mine.length) return;
    box.append(el('div', { class: 'meta', text: path ? 'Links already made for this:' : 'Links you shared:' }));
    for (const s of mine) {
      const row = el('div', { class: 'share-row' },
        el('div', { class: 'name-wrap' }, el('span', { class: 'name', text: baseName(s.path) }),
          el('span', { class: 'meta', text: `Expires ${new Date(s.expires).toLocaleString()} · opened ${s.opens} time${s.opens === 1 ? '' : 's'}` })),
        el('button', { class: 'btn btn-secondary', type: 'button', title: 'Copy link', onclick: () => copyText(s.url) }, icon('copy')),
        el('button', { class: 'btn btn-danger', type: 'button', title: 'Stop this link', onclick: async () => {
          if ((await postJson('/api/share/revoke', { token: s.token })).ok) row.remove();
        } }, icon('x')));
      box.append(row);
    }
  } catch { }
}

async function createShare() {
  const btn = $('btnShareCreate');
  btn.disabled = true;
  try {
    const res = await postJson('/api/share', { path: sharePath, hours: +$('shareHours').value });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) { $('shareNote').textContent = data.error || explainFailure(res); return; }
    $('shareUrl').value = data.url;
    $('shareResult').hidden = false;
    $('shareNote').textContent = `Works until ${new Date(data.expires).toLocaleString()}.` + (data.localOnly ? ' This address only works on your home network (see "Remote access" in the app to share outside it).' : '');
    $('shareUrl').select();
    copyText(data.url, true);
    loadShareList(sharePath);
  } finally { btn.disabled = false; }
}

async function copyText(text, quiet) {
  try { await navigator.clipboard.writeText(text); if (!quiet) showMessage('Link copied.'); else $('shareNote').textContent += ' Copied to the clipboard.'; }
  catch { if (!quiet) prompt('Copy this link:', text); }
}

// ------------------------------------------------------------------ find subtitles (OpenSubtitles)

async function openSubtitleFinder(m) {
  $('subsModal').hidden = false;
  $('subsStatus').textContent = 'Searching OpenSubtitles.com…';
  const list = $('subsResults');
  list.replaceChildren();
  try {
    const res = await postJson('/api/subtitles/search', { path: m.path });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) { $('subsStatus').textContent = data.error || explainFailure(res); return; }
    if (!data.results.length) { $('subsStatus').textContent = 'No subtitles found for this video.'; return; }
    $('subsStatus').textContent = 'Pick one. "Exact match" means it was made for this very file.';
    list.replaceChildren(...data.results.map(r => el('button', { class: 'tv-device', type: 'button', onclick: () => downloadSubtitle(m, r) },
      icon('cc-search'), el('span', {}, el('span', { class: 'name', text: r.name }),
        el('span', { class: 'meta', text: [r.lang.toUpperCase(), r.exact ? 'Exact match' : '', `${r.downloads.toLocaleString()} downloads`].filter(Boolean).join(' · ') })))));
  } catch { $('subsStatus').textContent = 'Could not search for subtitles.'; }
}

async function downloadSubtitle(m, r) {
  $('subsStatus').textContent = 'Downloading…';
  const res = await postJson('/api/subtitles/download', { path: m.path, id: r.id, lang: r.lang });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) { $('subsStatus').textContent = data.error || explainFailure(res); return; }
  $('subsModal').hidden = true;
  // Add it to the menu and switch it on.
  const sel = $('subsSelect');
  sel.append(el('option', { value: data.src, text: `${r.lang.toUpperCase()} (downloaded)` }));
  sel.hidden = false; $('btnSubStyle').hidden = false;
  sel.value = data.src;
  P.subtitle = data.src;
  store.set('localStorage', 'subsOn', '1');
  setSubtitleTrack();
  notice('Subtitles saved next to the video: ' + data.name);
}

// ------------------------------------------------------------------ subtitle style

function applySubStyle() {
  const p = $('player');
  p.dataset.subSize = store.get('localStorage', 'subSize', '1.1');
  p.dataset.subColor = store.get('localStorage', 'subColor', '#ffffff');
  p.dataset.subBack = store.get('localStorage', 'subBack', '0.6');
  const prev = $('subPreview');
  prev.parentElement.dataset.subSize = p.dataset.subSize;
  prev.parentElement.dataset.subColor = p.dataset.subColor;
  prev.parentElement.dataset.subBack = p.dataset.subBack;
}

function openSubStyle() {
  $('subSize').value = store.get('localStorage', 'subSize', '1.1');
  $('subColor').value = store.get('localStorage', 'subColor', '#ffffff');
  $('subBack').value = store.get('localStorage', 'subBack', '0.6');
  applySubStyle();
  $('subStyleModal').hidden = false;
}

// ------------------------------------------------------------------ setup

function initLibrary() {
  // Account menu
  $('btnUser').addEventListener('click', (e) => { e.stopPropagation(); $('userPop').hidden = !$('userPop').hidden; });
  document.addEventListener('click', (e) => { if (!e.target.closest('#userMenu')) $('userPop').hidden = true; });
  $('btnLogout').addEventListener('click', async () => {
    try { await fetch('/api/logout', { method: 'POST' }); } catch { }
    location.href = '/login';
  });
  $('btnMyLinks').addEventListener('click', () => {
    $('userPop').hidden = true;
    sharePath = '';
    $('shareTitle').textContent = 'Links you shared';
    $('shareResult').hidden = true; $('shareNote').textContent = '';
    $('btnShareCreate').hidden = true;
    $('shareModal').hidden = false;
    loadShareList('');
  });

  // Dialogs: close buttons and clicking outside
  const closers = { shareModal: 'btnShareClose', playlistModal: 'btnPlaylistClose', subsModal: 'btnSubsClose', subStyleModal: 'btnSubStyleClose', infoModal: 'btnInfoClose' };
  for (const [modal, button] of Object.entries(closers)) {
    $(button).addEventListener('click', () => { $(modal).hidden = true; });
    $(modal).addEventListener('click', (e) => { if (e.target === $(modal)) $(modal).hidden = true; });
  }
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    const open = Array.from(document.querySelectorAll('.modal:not([hidden])')).pop();
    if (open) { open.hidden = true; e.stopImmediatePropagation(); e.preventDefault(); }
  }, true);

  $('btnShareCreate').addEventListener('click', createShare);
  $('btnShareCopy').addEventListener('click', () => copyText($('shareUrl').value));
  $('btnNewPlaylist').addEventListener('click', createPlaylistFromPicker);
  $('newPlaylistName').addEventListener('keydown', (e) => { if (e.key === 'Enter') createPlaylistFromPicker(); });

  for (const [id, key] of [['subSize', 'subSize'], ['subColor', 'subColor'], ['subBack', 'subBack']])
    $(id).addEventListener('change', () => { store.set('localStorage', key, $(id).value); applySubStyle(); });
  applySubStyle();

  initPlayerExtras();

  updateNavTabs();
}

// ------------------------------------------------------------------ player extras: speed, sleep timer,
// picture-in-picture, "Skip intro" and the next-episode countdown

const X = { rate: 1, sleepAt: 0, sleepEnd: false, countdown: 0, countTimer: 0, next: null, dismissed: false };

function initPlayerExtras() {
  const v = video();
  const speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
  $('speedChips').replaceChildren(...speeds.map(r => el('button', { type: 'button', class: 'chip', 'data-rate': String(r), text: r === 1 ? 'Normal' : r + '×', onclick: () => setRate(r) })));
  const sleeps = [['Off', 0], ['15 min', 15], ['30 min', 30], ['45 min', 45], ['1 hour', 60], ['End of this', -1]];
  $('sleepChips').replaceChildren(...sleeps.map(([label, min]) => el('button', { type: 'button', class: 'chip', 'data-min': String(min), text: label, onclick: () => setSleep(min) })));
  markChips();

  $('btnMore').addEventListener('click', (e) => { e.stopPropagation(); $('playerMenu').hidden = !$('playerMenu').hidden; markChips(); });
  document.addEventListener('click', (e) => { if (!e.target.closest('#playerMenu, #btnMore')) $('playerMenu').hidden = true; });
  $('btnPip').addEventListener('click', async () => {
    $('playerMenu').hidden = true;
    try { document.pictureInPictureElement ? await document.exitPictureInPicture() : await v.requestPictureInPicture(); }
    catch { notice('Picture in picture is not available for this video.'); }
  });
  $('btnOffline').addEventListener('click', () => { $('playerMenu').hidden = true; if (P.media) toggleOffline(P.media); });

  // Speed survives the next file and conversions restarting.
  v.addEventListener('loadedmetadata', () => { v.playbackRate = X.rate; });
  v.addEventListener('ratechange', () => { if (v.playbackRate !== X.rate && v.readyState > 0 && !v.seeking) { X.rate = v.playbackRate; markChips(); } });

  v.addEventListener('timeupdate', () => {
    const m = P.media;
    if (!m) return;
    const t = currentTime();
    // Sleep timer
    if (X.sleepAt && Date.now() >= X.sleepAt) { stopForSleep(); }
    else if (X.sleepAt) $('sleepBadge').textContent = 'Sleep ' + formatTime((X.sleepAt - Date.now()) / 1000);
    // Skip intro
    const intro = m.intro;
    $('btnSkipIntro').hidden = !(intro && t >= intro.start && t < intro.end - 2);
    // Credits: offer the next episode a little early
    if (m.kind === 'video' && m.credits && t >= m.credits && !X.countTimer && !X.dismissed && !X.sleepEnd) startNextUp();
  });
  v.addEventListener('ended', () => {
    if (X.sleepEnd) { stopForSleep(); return; }
    if (P.media && P.media.kind === 'video' && !P.queue && !X.countTimer && !X.dismissed) startNextUp();
  });
  $('btnSkipIntro').addEventListener('click', () => { if (P.media && P.media.intro) seekTo(P.media.intro.end); $('btnSkipIntro').hidden = true; });
  $('btnNextNow').addEventListener('click', () => playNextUp());
  $('btnNextCancel').addEventListener('click', () => { cancelNextUp(); X.dismissed = true; });
}

/** Called by openMedia for every file. */
function onMediaOpened(m) {
  cancelNextUp();
  X.dismissed = false;
  X.next = null;
  if (m.kind === 'video') {
    // The next episode: from the show page's list, else what the server found in the same show.
    const list = P.episodes;
    const i = list ? list.findIndex(e => e.path === m.path) : -1;
    if (i >= 0 && i < list.length - 1) X.next = { path: list[i + 1].path, title: list[i + 1].title };
    else if (m.next) X.next = { path: m.next.path, title: `${m.next.show} · ${m.next.title}` };
  }
  $('btnSkipIntro').hidden = true;
  $('btnPip').hidden = !(m.kind === 'video' && document.pictureInPictureEnabled);
  updateOfflineButton(m);
}

function setRate(r) {
  X.rate = r;
  video().playbackRate = r;
  markChips();
  $('playerMenu').hidden = true;
  notice(r === 1 ? 'Normal speed' : `Speed ${r}×`);
}

function setSleep(min) {
  X.sleepAt = min > 0 ? Date.now() + min * 60000 : 0;
  X.sleepEnd = min === -1;
  const b = $('sleepBadge');
  b.hidden = min === 0;
  b.textContent = X.sleepEnd ? 'Sleep at end' : 'Sleep ' + formatTime(min * 60);
  markChips();
  $('playerMenu').hidden = true;
  notice(min === 0 ? 'Sleep timer off' : X.sleepEnd ? 'Playback stops when this one ends.' : `Playback stops in ${min} minutes.`);
}

function stopForSleep() {
  video().pause();
  X.sleepAt = 0; X.sleepEnd = false;
  $('sleepBadge').hidden = true;
  cancelNextUp();
  markChips();
  notice('Sleep timer: paused. Good night!');
}

function markChips() {
  document.querySelectorAll('#speedChips .chip').forEach(c => c.classList.toggle('on', +c.dataset.rate === X.rate));
  const cur = X.sleepEnd ? -1 : X.sleepAt ? null : 0;
  document.querySelectorAll('#sleepChips .chip').forEach(c => c.classList.toggle('on', cur !== null && +c.dataset.min === cur));
}

function startNextUp() {
  if (!X.next || P.queue) return;
  X.countdown = 10;
  $('playerMenu').hidden = true;
  $('nextUpTitle').textContent = X.next.title;
  $('nextUpCount').textContent = `(${X.countdown})`;
  $('nextUp').hidden = false;
  X.countTimer = setInterval(() => {
    X.countdown--;
    $('nextUpCount').textContent = `(${X.countdown})`;
    if (X.countdown <= 0) playNextUp();
  }, 1000);
}

function cancelNextUp() {
  clearInterval(X.countTimer);
  X.countTimer = 0;
  $('nextUp').hidden = true;
}

function playNextUp() {
  const n = X.next;
  cancelNextUp();
  if (n) openMedia(n.path);
}

// ------------------------------------------------------------------ save for offline (phones, trips)
// Files are kept by the service worker's offline store; the list of what is saved is kept here.
// Browsers only allow this over HTTPS (or on the PC itself), so the option shows only there.

const offlineSupported = () => 'serviceWorker' in navigator && window.isSecureContext && 'caches' in window;

function offlineIndex() {
  try { return JSON.parse(store.get('localStorage', 'offlineIndex', '[]')) || []; } catch { return []; }
}
function saveOfflineIndex(list) { store.set('localStorage', 'offlineIndex', JSON.stringify(list)); }
function isSavedOffline(path) { return offlineIndex().some(e => e.path === path); }

function updateOfflineButton(m) {
  const b = $('btnOffline');
  const can = offlineSupported() && m.kind !== 'image' && !(me && me.guest);
  b.hidden = !can;
  if (!can) return;
  const saved = isSavedOffline(m.path);
  b.disabled = !saved && !m.direct;
  $('offlineLabel').textContent = saved ? 'Remove from offline' : m.direct ? 'Save for offline' : "Can't save offline (this file is converted to play)";
}

async function toggleOffline(m) {
  const cache = await caches.open('localstream-offline');
  if (isSavedOffline(m.path)) {
    await removeOffline(m.path);
    notice('Removed from offline.');
  } else {
    try {
      const est = navigator.storage && navigator.storage.estimate ? await navigator.storage.estimate() : null;
      const size = m.size || 0;
      if (est && size && est.quota - est.usage < size * 1.1) { notice('Not enough space on this device to save it.'); return; }
      if (navigator.storage && navigator.storage.persist) navigator.storage.persist().catch(() => {});
      notice('Saving for offline… (keep this page open)');
      $('btnOffline').disabled = true;
      await cache.add(m.file);
      try { await cache.add(m.thumb); } catch { }
      const list = offlineIndex().filter(e => e.path !== m.path);
      const t = P.queue ? P.queue[P.index] : null;
      list.unshift({ path: m.path, name: m.name, title: t ? t.title : m.name.replace(/\.[^.]+$/, ''), artist: t ? t.artist : '', kind: m.kind, file: m.file, thumb: m.thumb, duration: m.duration, size, saved: Date.now() });
      saveOfflineIndex(list);
      notice('Saved. It plays even without a connection (see "Saved" on the home page).');
    } catch (e) {
      notice('Could not save it: ' + (e && e.name === 'QuotaExceededError' ? 'not enough space.' : 'the download failed.'));
    }
  }
  if (P.media) updateOfflineButton(P.media);
  updateNavTabs();
}

async function removeOffline(path) {
  const e = offlineIndex().find(x => x.path === path);
  if (!e) return;
  const cache = await caches.open('localstream-offline');
  await cache.delete(e.file); await cache.delete(e.thumb);
  saveOfflineIndex(offlineIndex().filter(x => x.path !== path));
}

/** What openMedia uses when the PC can't be reached but the file is saved. */
function offlineMedia(path) {
  const e = offlineIndex().find(x => x.path === path);
  if (!e) return null;
  return { name: e.name, path: e.path, kind: e.kind, file: e.file, thumb: e.thumb, duration: e.duration || 0, direct: true, canConvert: false,
           audio: [], subtitles: [], resume: null, offline: true, size: e.size };
}

async function loadOffline() {
  pageHeader('download', 'Saved for offline', [['Saved', null]]);
  const c = $('container');
  fixLayout(c, 'songs');
  const list = offlineIndex();
  if (!offlineSupported()) { showMessage('Saving for offline needs the secure (https://) address of LocalStream. Turn on HTTPS in the LocalStream app and open that address on this phone.', true); return; }
  let usage = '';
  try { const est = await navigator.storage.estimate(); usage = ` · using ${formatSize(est.usage || 0)} of this device's space`; } catch { }
  document.querySelectorAll('.page-tools').forEach(n => n.remove());
  c.before(el('div', { class: 'page-tools' }, el('span', { class: 'meta', text: `${list.length} saved${usage}` })));
  if (!list.length) { showMessage('Nothing saved yet. In the player, open ⋮ and choose "Save for offline".'); c.replaceChildren(); return; }
  const songs = list.filter(e => e.kind === 'audio');
  const queue = songs.map(e => ({ path: e.path, title: e.title, artist: e.artist, album: '', thumb: e.thumb }));
  c.replaceChildren(...list.map((e) => {
    const t = { title: e.title, artist: e.artist || (e.kind === 'video' ? 'Video' : ''), album: formatSize(e.size || 0), track: 0, item: { path: e.path, name: e.name, file: e.file, thumb: e.thumb, kind: e.kind } };
    const row = songRow(t, Math.max(0, songs.indexOf(e)), queue, true, e.kind === 'audio' ? null : () => openMedia(e.path));
    row.querySelector('.actions').replaceChildren(actionButton('trash', 'Remove from this device', async (ev) => {
      ev.stopPropagation(); await removeOffline(e.path); row.remove();
    }, true));
    return row;
  }));
}
