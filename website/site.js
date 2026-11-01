// Corewright homepage: the core map in the hero.
// Five scenes show what Autopilot and the rule engine do on a 16-core, two-CCD CPU.
// The page works without this script (it shows the desktop scene).
(function () {
  'use strict';

  var root = document.querySelector('.package');
  if (!root) return;

  var threads = root.querySelectorAll('.t');
  var cores = root.querySelectorAll('.core');
  var ccds = root.querySelectorAll('.ccd');
  var buttons = root.querySelectorAll('[data-scene]');
  var caption = root.querySelector('#die-caption');
  var legendNote = ' <span class="legend-inline">★ marks the fastest core of each CCD, • the second fastest.</span>';

  // Small deterministic generator so every visit shows the same picture.
  function rng(seed) {
    return function () {
      seed = (seed * 1664525 + 1013904223) >>> 0;
      return seed / 4294967296;
    };
  }

  function range(r, lo, hi) { return Math.round(lo + r() * (hi - lo)); }

  // Each scene: per thread [kind, load], kept-free cores, CCD tags, caption.
  function scene(name) {
    var r = rng(name.length * 7919 + 17);
    var t = [];
    var free = [];
    var tags = ['', ''];
    var i;
    var gameOn = name === 'game' || name === 'build' || name === 'hog';

    for (i = 0; i < 32; i++) t.push(['idle', range(r, 3, 22)]);

    if (gameOn) {
      for (i = 0; i < 16; i++) t[i] = ['game', range(r, 48, 94)];
      tags[0] = 'game.exe: high priority, fastest CCD';
    }
    if (name === 'game') {
      for (i = 16; i < 24; i++) t[i] = ['bg', range(r, 14, 42)];
      tags[1] = 'Background work moved here';
    }
    if (name === 'build' || name === 'hog') {
      free = [8, 9];
      for (i = 16; i < 20; i++) t[i] = ['idle', range(r, 2, 9)];
      for (i = 20; i < 32; i++) t[i] = ['build', range(r, 82, 99)];
      tags[1] = 'cl.exe, link.exe: below normal, low I/O';
    }
    if (name === 'hog') {
      for (i = 20; i < 24; i++) t[i] = ['held', range(r, 28, 46)];
      tags[1] = 'updater.exe held back; builds keep running';
    }
    return { threads: t, free: free, tags: tags };
  }

  var captions = {
    desktop: 'Nothing to do. Corewright looks once every two seconds and otherwise stays asleep.',
    game: 'A game starts. Autopilot finds it in your game library, raises its priority and gives it CCD 0, the faster of the two by the CPPC ranking. Busy background work moves to CCD 1.',
    build: 'You start a build. Compilers run below normal with low I/O priority on free cores of CCD 1. The game’s CCD and the strongest cores stay clear.',
    hog: 'An updater keeps taking CPU in the background. Balancing lowers its priority until it calms down. The window you use is never touched.',
    exit: 'The game exits. Every setting Corewright changed goes back to the value it had before.'
  };

  function show(name) {
    var s = scene(name === 'exit' ? 'desktop' : name);
    var i;
    for (i = 0; i < threads.length; i++) {
      threads[i].setAttribute('data-k', s.threads[i][0]);
      threads[i].style.setProperty('--l', (s.threads[i][1] / 100).toFixed(2));
    }
    for (i = 0; i < cores.length; i++) {
      if (s.free.indexOf(i) >= 0) cores[i].setAttribute('data-free', '');
      else cores[i].removeAttribute('data-free');
    }
    for (i = 0; i < ccds.length; i++) {
      var sum = 0;
      for (var j = i * 16; j < i * 16 + 16; j++) sum += s.threads[j][1];
      ccds[i].querySelector('[data-ccd-load]').textContent = Math.round(sum / 16) + ' %';
      var tag = ccds[i].querySelector('[data-ccd-tag]');
      tag.textContent = s.tags[i];
      ccds[i].toggleAttribute('data-claimed', s.tags[i] !== '');
    }
    root.toggleAttribute('data-reverted', name === 'exit');
    for (i = 0; i < buttons.length; i++) {
      buttons[i].setAttribute('aria-pressed', buttons[i].getAttribute('data-scene') === name ? 'true' : 'false');
    }
    caption.innerHTML = captions[name] + legendNote;
  }

  // One guided pass through the scenes on load, unless the visitor prefers
  // reduced motion. Any interaction with the scene buttons ends it.
  var order = ['desktop', 'game', 'build', 'hog', 'exit'];
  var step = 0;
  var timer = null;

  function stop() {
    if (timer) { clearInterval(timer); timer = null; }
  }

  for (var b = 0; b < buttons.length; b++) {
    buttons[b].addEventListener('click', function (e) {
      stop();
      caption.setAttribute('aria-live', 'polite');
      show(e.currentTarget.getAttribute('data-scene'));
    });
  }

  show('desktop');

  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  if (!reduced) {
    timer = setInterval(function () {
      if (document.hidden) return;
      step++;
      show(order[step]);
      if (step >= order.length - 1) stop();
    }, 4200);
  }
})();
