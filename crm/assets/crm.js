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
})();
