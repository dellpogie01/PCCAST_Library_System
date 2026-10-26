(function () {
  'use strict';
  var root = document.documentElement;

  // ----- Theme switch (light / dark / system), remembered in localStorage -----
  var media = window.matchMedia('(prefers-color-scheme: dark)');
  function applyTheme(choice) {
    var dark = choice === 'dark' || (choice === 'system' && media.matches);
    root.setAttribute('data-theme', choice);
    root.setAttribute('data-resolved-theme', dark ? 'dark' : 'light');
    document.querySelectorAll('[data-theme-choice]').forEach(function (b) {
      b.setAttribute('aria-pressed', String(b.getAttribute('data-theme-choice') === choice));
    });
  }
  applyTheme(root.getAttribute('data-theme') || 'system');
  document.querySelectorAll('[data-theme-choice]').forEach(function (button) {
    button.addEventListener('click', function () {
      var choice = button.getAttribute('data-theme-choice');
      try { localStorage.setItem('pccast-theme', choice); } catch (e) { /* ignore */ }
      applyTheme(choice);
    });
  });
  media.addEventListener('change', function () {
    if ((root.getAttribute('data-theme') || 'system') === 'system') applyTheme('system');
  });

  // ----- Mobile navigation drawer -----
  var menuButton = document.querySelector('[data-drawer-toggle]');
  function setDrawer(open) {
    document.body.classList.toggle('drawer-open', open);
    if (menuButton) menuButton.setAttribute('aria-expanded', String(open));
  }
  if (menuButton) menuButton.addEventListener('click', function () { setDrawer(!document.body.classList.contains('drawer-open')); });
  document.querySelectorAll('[data-drawer-close]').forEach(function (el) { el.addEventListener('click', function () { setDrawer(false); }); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      setDrawer(false);
      document.querySelectorAll('details.account[open]').forEach(function (d) { d.open = false; });
    }
  });
  document.addEventListener('click', function (e) {
    document.querySelectorAll('details.account[open]').forEach(function (d) { if (!d.contains(e.target)) d.open = false; });
  });

  // ----- Philippine clock (Asia/Manila) and midnight refresh -----
  var zone = 'Asia/Manila';
  var dateFmt = new Intl.DateTimeFormat('en-PH', { timeZone: zone, weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
  var timeFmt = new Intl.DateTimeFormat('en-PH', { timeZone: zone, hour: 'numeric', minute: '2-digit', second: '2-digit', hour12: true });
  var dayFmt = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' });
  var loadedDay = dayFmt.format(new Date());
  function tick() {
    var now = new Date();
    document.querySelectorAll('[data-clock-date]').forEach(function (el) { el.textContent = dateFmt.format(now); });
    document.querySelectorAll('[data-clock-time]').forEach(function (el) { el.textContent = timeFmt.format(now) + ' PHT'; });
    // Date-dependent figures (due today, overdue, fines) are recalculated by the server after Manila midnight.
    if (document.querySelector('[data-reload-at-midnight]') && dayFmt.format(now) !== loadedDay) window.location.reload();
  }
  tick();
  setInterval(tick, 1000);

  // ----- Confirmation dialogs for destructive actions -----
  var dialog = document.getElementById('confirm-dialog');
  if (dialog && typeof dialog.showModal === 'function') {
    document.addEventListener('submit', function (e) {
      var submitter = e.submitter;
      var message = submitter && submitter.getAttribute('data-confirm');
      if (!message || e.target.dataset.confirmed === 'true') return;
      e.preventDefault();
      dialog.querySelector('[data-confirm-message]').textContent = message;
      dialog.querySelector('[data-confirm-ok]').textContent = submitter.getAttribute('data-confirm-label') || 'Confirm';
      var form = e.target;
      dialog.returnValue = '';
      dialog.addEventListener('close', function onClose() {
        dialog.removeEventListener('close', onClose);
        if (dialog.returnValue === 'ok') {
          form.dataset.confirmed = 'true';
          if (form.requestSubmit) form.requestSubmit(submitter); else form.submit();
        }
      });
      dialog.showModal();
    });
  }

  // ----- Prevent double submission (the server also enforces this) -----
  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (e.defaultPrevented || form.method.toLowerCase() !== 'post') return;
    if (form.dataset.submitting === 'true') { e.preventDefault(); return; }
    form.dataset.submitting = 'true';
    setTimeout(function () {
      form.querySelectorAll('button[type=submit]:not([data-keep-enabled])').forEach(function (b) { b.disabled = true; });
    }, 0);
  });
  window.addEventListener('pageshow', function (e) {
    if (e.persisted) document.querySelectorAll('form[data-submitting]').forEach(function (f) {
      delete f.dataset.submitting;
      f.querySelectorAll('button[type=submit]').forEach(function (b) { b.disabled = false; });
    });
  });
})();
