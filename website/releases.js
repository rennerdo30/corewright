// Corewright homepage: fills in the newest stable and beta release from GitHub.
// Without this script (or without network) the links still go to the GitHub releases pages.
(function () {
  'use strict';

  var API = 'https://api.github.com/repos/rennerdo30/corewright/releases?per_page=30';
  var REPO = 'https://github.com/rennerdo30/corewright';

  // SemVer comparison for "0.1.0" and "0.1.0-beta.2".
  function parse(tag) {
    var m = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.]+))?$/.exec(tag || '');
    if (!m) return null;
    return { n: [+m[1], +m[2], +m[3]], pre: m[4] ? m[4].split('.') : [] };
  }
  function compare(a, b) {
    for (var i = 0; i < 3; i++) if (a.n[i] !== b.n[i]) return a.n[i] - b.n[i];
    if (!a.pre.length || !b.pre.length) return (a.pre.length ? 0 : 1) - (b.pre.length ? 0 : 1);
    for (var j = 0; j < Math.min(a.pre.length, b.pre.length); j++) {
      var x = a.pre[j], y = b.pre[j];
      if (x === y) continue;
      var nx = /^\d+$/.test(x), ny = /^\d+$/.test(y);
      if (nx && ny) return +x - +y;
      if (nx !== ny) return nx ? -1 : 1;
      return x < y ? -1 : 1;
    }
    return a.pre.length - b.pre.length;
  }

  function pick(releases, beta) {
    var best = null, bestV = null;
    releases.forEach(function (r) {
      if (r.draft || (!beta && r.prerelease)) return;
      if (beta && !r.prerelease) return;
      var v = parse(r.tag_name);
      if (!v) return;
      if (!bestV || compare(v, bestV) > 0) { best = r; bestV = v; }
    });
    return best;
  }

  function setup(r) {
    var assets = r.assets || [];
    for (var i = 0; i < assets.length; i++)
      if (/^Corewright-.*-setup\.exe$/.test(assets[i].name) && assets[i].browser_download_url.indexOf(REPO + '/releases/download/') === 0)
        return assets[i].browser_download_url;
    return r.html_url;
  }

  function fill(kind, r) {
    document.querySelectorAll('[data-release="' + kind + '"]').forEach(function (el) {
      var field = el.getAttribute('data-field');
      if (!r) {
        if (field === 'missing') el.hidden = false;
        if (field === 'found') el.hidden = true;
        return;
      }
      if (field === 'missing') el.hidden = true;
      if (field === 'found') el.hidden = false;
      if (field === 'version') el.textContent = r.tag_name.replace(/^v/, '');
      if (field === 'date') el.textContent = new Date(r.published_at).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
      if (field === 'download') el.href = setup(r);
      if (field === 'notes') el.href = r.html_url;
    });
  }

  if (!document.querySelector('[data-release]') || !window.fetch) return;
  fetch(API, { headers: { Accept: 'application/vnd.github+json' } })
    .then(function (res) { return res.ok ? res.json() : []; })
    .then(function (list) {
      if (!Array.isArray(list)) list = [];
      fill('stable', pick(list, false));
      fill('beta', pick(list, true));
    })
    .catch(function () { /* keep the static links */ });
})();
