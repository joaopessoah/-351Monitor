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

  /* ---------- UTM: primeira origem da visita fica na sessão ---------- */
  try {
    var params = new URLSearchParams(window.location.search);
    if (params.get('utm_source') && !sessionStorage.getItem('m351_utm')) {
      sessionStorage.setItem('m351_utm', JSON.stringify({
        source: params.get('utm_source') || '',
        medium: params.get('utm_medium') || '',
        campaign: params.get('utm_campaign') || ''
      }));
    }
  } catch (e) { /* sessionStorage indisponível — segue sem UTM */ }

  /* ---------- Formulário de contato → CRM (/crm/intake.php) ---------- */
  var form = document.getElementById('form-demo');
  if (form) {
    var tsField = form.querySelector('input[name="form_ts"]');
    if (tsField) { tsField.value = String(Date.now()); }
    try {
      var utm = JSON.parse(sessionStorage.getItem('m351_utm') || 'null');
      if (utm) {
        form.querySelector('input[name="utm_source"]').value = utm.source || '';
        form.querySelector('input[name="utm_medium"]').value = utm.medium || '';
        form.querySelector('input[name="utm_campaign"]').value = utm.campaign || '';
      }
    } catch (e) { /* sem UTM */ }

    var msg = document.getElementById('form-msg');
    var showMsg = function (html) {
      if (!msg) { return; }
      msg.innerHTML = html;
      msg.className = 'form-msg form-msg--erro';
      msg.hidden = false;
    };
    var setFieldError = function (name, text) {
      var el = form.querySelector('.field-error[data-for="' + name + '"]');
      if (el) { el.textContent = text; el.hidden = false; }
    };
    var clearFieldErrors = function () {
      var els = form.querySelectorAll('.field-error');
      for (var i = 0; i < els.length; i++) { els[i].textContent = ''; els[i].hidden = true; }
    };

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      clearFieldErrors();
      if (msg) { msg.hidden = true; }
      var btn = form.querySelector('button[type="submit"]');
      var btnHtml = btn.innerHTML;
      btn.disabled = true;
      btn.textContent = 'Enviando…';
      var done = function () { btn.disabled = false; btn.innerHTML = btnHtml; };

      fetch('/crm/intake.php', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          nome: form.querySelector('[name="nome"]').value,
          empresa: form.querySelector('[name="empresa"]').value,
          email: form.querySelector('[name="email"]').value,
          whatsapp: form.querySelector('[name="whatsapp"]').value,
          estacoes: form.querySelector('[name="estacoes"]').value,
          site_web: form.querySelector('[name="site_web"]').value,
          form_ts: tsField && tsField.value ? Number(tsField.value) : null,
          utm_source: form.querySelector('[name="utm_source"]').value,
          utm_medium: form.querySelector('[name="utm_medium"]').value,
          utm_campaign: form.querySelector('[name="utm_campaign"]').value
        })
      }).then(function (res) {
        return res.json().then(function (body) { return { status: res.status, body: body }; });
      }).then(function (r) {
        if (r.body && r.body.ok) {
          form.innerHTML = '<p class="form-success"><strong>Recebemos seus dados!</strong> A Bruna chama você no WhatsApp em até 1 dia útil.</p>';
          return;
        }
        if (r.body && r.body.errors) {
          for (var k in r.body.errors) {
            if (Object.prototype.hasOwnProperty.call(r.body.errors, k)) { setFieldError(k, r.body.errors[k]); }
          }
          showMsg('Confira os campos destacados e tente de novo.');
        } else {
          showMsg((r.body && r.body.message) ? r.body.message : 'Não foi possível enviar. Tente de novo em instantes.');
        }
        done();
      }).catch(function () {
        showMsg('Não foi possível enviar agora. Chame direto no <a href="https://wa.me/5511992209235" target="_blank" rel="noopener">WhatsApp</a>.');
        done();
      });
    });
  }
})();
