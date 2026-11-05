'use strict';
// Sign-in form: POST /api/login sets a cookie; then back to the page that asked for the login.
document.addEventListener('DOMContentLoaded', () => {
  try { const t = localStorage.getItem('theme'); if (t) document.documentElement.setAttribute('data-theme', t); } catch { }
  const form = document.getElementById('loginForm'), err = document.getElementById('loginError'), btn = document.getElementById('btnLogin');
  document.getElementById('username').focus();
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    err.hidden = true;
    btn.disabled = true;
    try {
      const res = await fetch('/api/login', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: form.username.value, password: form.password.value, remember: document.getElementById('remember').checked }),
      });
      if (res.ok) {
        const next = new URLSearchParams(location.search).get('next') || '/';
        location.replace(next.startsWith('/') && !next.startsWith('//') ? next : '/'); // only our own pages
        return;
      }
      let msg = 'Sign-in failed.';
      try { msg = (await res.json()).error || msg; } catch { }
      err.textContent = msg;
      err.hidden = false;
      form.password.select();
    } catch {
      err.textContent = 'Could not reach the LocalStream server.';
      err.hidden = false;
    } finally { btn.disabled = false; }
  });
});
