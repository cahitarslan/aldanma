// Applies the saved theme and text size before the first paint, so the page never flashes.
// Light theme is the default on purpose; dark is opt-in and remembered on this device only.
(function () {
  var root = document.documentElement;
  try {
    var theme = localStorage.getItem('aldanma.theme');
    var size = localStorage.getItem('aldanma.size');
    if (theme === 'dark') root.setAttribute('data-theme', 'dark');
    if (size === 'large' || size === 'xlarge') root.setAttribute('data-size', size);
  } catch (e) { /* storage blocked: keep defaults */ }
})();
