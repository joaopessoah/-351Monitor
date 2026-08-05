/* +351 Monitor — interações da landing */
(function () {
  'use strict';

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /* ---------- Nav: fundo ao rolar ---------- */
  var nav = document.querySelector('.nav');
  function onScroll() {
    if (window.scrollY > 12) { nav.classList.add('scrolled'); }
    else { nav.classList.remove('scrolled'); }
  }
  window.addEventListener('scroll', onScroll, { passive: true });
  onScroll();

  /* ---------- Menu mobile ---------- */
  var burger = document.querySelector('.nav-burger');
  var mobile = document.querySelector('.nav-mobile');
  if (burger && mobile) {
    burger.addEventListener('click', function () {
      var open = burger.getAttribute('aria-expanded') === 'true';
      burger.setAttribute('aria-expanded', String(!open));
      burger.setAttribute('aria-label', open ? 'Abrir menu' : 'Fechar menu');
      mobile.hidden = open;
    });
    mobile.addEventListener('click', function (e) {
      if (e.target.tagName === 'A') {
        burger.setAttribute('aria-expanded', 'false');
        mobile.hidden = true;
      }
    });
  }

  /* ---------- Reveal ao rolar ---------- */
  var revealEls = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window && !reduceMotion) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add('on');
          io.unobserve(entry.target);
        }
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });
    revealEls.forEach(function (el) { io.observe(el); });
  } else {
    revealEls.forEach(function (el) { el.classList.add('on'); });
  }

  /* ---------- Pulso: desenho da linha ---------- */
  function drawPulse(path, duration, delay) {
    var len = path.getTotalLength();
    path.style.strokeDasharray = String(len);
    path.style.strokeDashoffset = String(len);
    path.getBoundingClientRect(); /* força reflow antes da transição */
    path.style.transition = 'stroke-dashoffset ' + duration + 'ms cubic-bezier(.4,0,.2,1) ' + (delay || 0) + 'ms';
    path.style.strokeDashoffset = '0';
  }

  if (!reduceMotion) {
    var heroPath = document.querySelector('.pulse-path');
    if (heroPath) { drawPulse(heroPath, 2200, 250); }

    var lazyPulses = document.querySelectorAll('.pulse-path-2, .pulse-path-3');
    if ('IntersectionObserver' in window) {
      var pio = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            drawPulse(entry.target, 1800, 100);
            pio.unobserve(entry.target);
          }
        });
      }, { threshold: 0.4 });
      lazyPulses.forEach(function (p) { pio.observe(p); });
    }
  }

  /* ---------- Ano no rodapé ---------- */
  var ano = document.getElementById('ano');
  if (ano) { ano.textContent = String(new Date().getFullYear()); }
})();
