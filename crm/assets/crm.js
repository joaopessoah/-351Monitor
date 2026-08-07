/* +351 CRM — interações mínimas (sem inline JS por causa do CSP) */
(function () {
  'use strict';

  /* Confirmação de ações destrutivas: <form data-confirm="mensagem"> */
  document.addEventListener('submit', function (e) {
    var f = e.target;
    if (f.hasAttribute && f.hasAttribute('data-confirm')) {
      if (!window.confirm(f.getAttribute('data-confirm'))) { e.preventDefault(); }
    }
  });

  document.addEventListener('change', function (e) {
    var el = e.target;

    /* Filtros: auto-submit ao mudar */
    if (el.classList.contains('auto-submit') && el.form) {
      el.form.submit();
      return;
    }

    /* Kanban: mover status; 'perdido' exige motivo */
    if (el.classList.contains('kanban-move') && el.form) {
      if (el.value === el.getAttribute('data-current')) { return; }
      if (el.value === 'perdido') {
        var motivo = window.prompt('Motivo da perda:');
        if (motivo === null || motivo.trim() === '') {
          el.value = el.getAttribute('data-current');
          return;
        }
        el.form.querySelector('input[name="lost_reason"]').value = motivo.trim();
      }
      el.form.submit();
    }
  });

  /* Detalhe do lead: campo de motivo aparece só quando status = perdido */
  var statusSel = document.getElementById('status-select');
  var lostWrap = document.getElementById('lost-reason-wrap');
  if (statusSel && lostWrap) {
    var sync = function () { lostWrap.hidden = statusSel.value !== 'perdido'; };
    statusSel.addEventListener('change', sync);
    sync();
  }

  /* ---------- CNPJ: máscara + validação ao vivo ---------- */
  /* Espelha o norm_cnpj do PHP: 12 posições alfanuméricas + 2 DVs numéricos
     (valor do caractere = ASCII - 48, pesos 2..9 da direita para a esquerda). */
  function cnpjLimpo(v) {
    return v.toUpperCase().replace(/[^0-9A-Z]/g, '').slice(0, 14);
  }
  function cnpjMascara(raw) {
    var out = '';
    for (var i = 0; i < raw.length; i++) {
      if (i === 2 || i === 5) { out += '.'; }
      if (i === 8) { out += '/'; }
      if (i === 12) { out += '-'; }
      out += raw[i];
    }
    return out;
  }
  function cnpjValido(raw) {
    if (!/^[0-9A-Z]{12}[0-9]{2}$/.test(raw)) { return false; }
    if (/^(.)\1{13}$/.test(raw)) { return false; }
    var dv = function (base) {
      var sum = 0, p = 2;
      for (var i = base.length - 1; i >= 0; i--) {
        sum += (base.charCodeAt(i) - 48) * p;
        p = p === 9 ? 2 : p + 1;
      }
      var r = sum % 11;
      return r < 2 ? 0 : 11 - r;
    };
    return Number(raw[12]) === dv(raw.slice(0, 12)) && Number(raw[13]) === dv(raw.slice(0, 13));
  }

  var live = document.getElementById('cnpj-live');
  var heroBtn = document.getElementById('cnpj-hero-btn');
  var heroInput = document.getElementById('cnpj-hero-input');
  var liveIdle = live ? live.textContent : '';

  function cnpjAtualiza(el) {
    var raw = cnpjLimpo(el.value);
    el.value = cnpjMascara(raw);
    if (el !== heroInput || !live) { return; }
    if (raw.length === 0) {
      live.dataset.state = 'idle';
      live.textContent = liveIdle;
    } else if (raw.length < 14) {
      live.dataset.state = 'idle';
      live.textContent = raw.length + '/14 caracteres…';
    } else if (cnpjValido(raw)) {
      live.dataset.state = 'ok';
      live.textContent = '✓ CNPJ válido — pode buscar.';
    } else {
      live.dataset.state = 'bad';
      live.textContent = 'Os dígitos verificadores não conferem — revise o número.';
    }
    if (heroBtn) { heroBtn.classList.toggle('is-ready', raw.length === 14 && cnpjValido(raw)); }
  }

  document.addEventListener('input', function (e) {
    if (e.target.classList && e.target.classList.contains('cnpj-mask')) { cnpjAtualiza(e.target); }
  });
  if (heroInput && heroInput.value) { cnpjAtualiza(heroInput); }

  /* Estado de carregamento ao buscar na Receita */
  var heroForm = document.getElementById('cnpj-start-form');
  if (heroForm && heroBtn) {
    heroForm.addEventListener('submit', function () {
      heroBtn.disabled = true;
      heroBtn.textContent = 'Buscando na Receita…';
    });
  }
})();
