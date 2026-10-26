// Applies the saved theme before first paint to avoid a flash of the wrong theme.
(function () {
  var stored = 'system';
  try { stored = localStorage.getItem('pccast-theme') || 'system'; } catch (e) { /* storage unavailable */ }
  var dark = stored === 'dark' || (stored === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  var root = document.documentElement;
  root.setAttribute('data-theme', stored);
  root.setAttribute('data-resolved-theme', dark ? 'dark' : 'light');
})();
