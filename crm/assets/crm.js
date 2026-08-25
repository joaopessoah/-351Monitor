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

  /* Registrar interação: "qual e-mail" aparece só quando o tipo é e-mail, e o
     resumo (obrigatório) já vem escrito para a cadência — dizer "mandei o
     terceiro e-mail" é digitação sem informação, o email_seq já grava isso.
     Só escrevemos no campo enquanto ele estiver vazio ou com o texto que nós
     mesmos pusemos: o que a pessoa digitar nunca é sobrescrito. */
  var intType = document.getElementById('int-type');
  var seqWrap = document.getElementById('int-email-seq-wrap');
  var intSeq = document.getElementById('int-email-seq');
  var intResumo = document.getElementById('int-summary');
  if (intType && seqWrap) {
    var autoResumo = '';

    var sugereResumo = function () {
      if (!intResumo || !intSeq) { return; }
      if (intResumo.value !== '' && intResumo.value !== autoResumo) { return; }
      var novo = '';
      if (intType.value === 'email' && intSeq.selectedIndex >= 0) {
        novo = intSeq.options[intSeq.selectedIndex].text + ' enviado (modelo padrão).';
      }
      intResumo.value = novo;
      autoResumo = novo;
    };

    var syncSeq = function () {
      seqWrap.hidden = intType.value !== 'email';
      sugereResumo();
    };
    intType.addEventListener('change', syncSeq);
    if (intSeq) { intSeq.addEventListener('change', sugereResumo); }
    syncSeq();
  }

  /* ---------- Quadro: arrastar e soltar ----------
     Progressive enhancement. Sem JS (ou em tela de toque, onde o drag&drop
     nativo do HTML5 nao funciona) cada card mostra o select "mover para" com
     botao de submit, que posta como qualquer outro form do CRM.
     Ao soltar mandamos a ORDEM COMPLETA da coluna de destino — inclusive os
     cards que o filtro escondeu, que ficam no DOM como placeholder — e o
     servidor renumera de 1 a N numa transacao. */
  var board = document.querySelector('.board');
  if (board && window.matchMedia && window.matchMedia('(pointer: fine)').matches) {
    board.classList.add('has-dnd');

    var arrastando = null;
    var origem = null;      // coluna de onde saiu, para desfazer
    var irmaoOrigem = null; // vizinho de baixo na origem, idem
    var ordemAntes = '';    // ordem da coluna de destino antes de mexer
    var soltou = false;

    var idsDe = function (drop) {
      return [].map.call(drop.querySelectorAll('.board-card'), function (c) { return c.dataset.id; });
    };

    var contagem = function () {
      [].forEach.call(board.querySelectorAll('.kanban-col'), function (col) {
        var n = col.querySelectorAll('.board-card:not([hidden])').length;
        var span = col.querySelector('h3 .n');
        if (span) { span.textContent = n; }
        var vazia = col.querySelector('.board-vazia');
        if (vazia) { vazia.hidden = n > 0; }
      });
    };

    var desfaz = function (card, pai, irmao) {
      /* O irmao pode ter saido do DOM no meio do caminho: nunca deixar o
         insertBefore estourar e engolir a mensagem de erro. */
      try {
        if (irmao && irmao.parentNode === pai) { pai.insertBefore(card, irmao); } else { pai.appendChild(card); }
      } catch (err) {
        window.location.reload();
        return;
      }
      contagem();
    };

    var alvoDepois = function (drop, y) {
      /* Card acima do qual o arrastado entra, pelo meio de cada card.
         :not([hidden]) porque placeholder de card filtrado tem altura zero. */
      var cards = [].slice.call(drop.querySelectorAll('.board-card:not(.is-dragging):not([hidden])'));
      for (var i = 0; i < cards.length; i++) {
        var r = cards[i].getBoundingClientRect();
        if (y < r.top + r.height / 2) { return cards[i]; }
      }
      return null;
    };

    board.addEventListener('dragstart', function (e) {
      var card = e.target.closest && e.target.closest('.board-card');
      if (!card) { return; }
      arrastando = card;
      origem = card.parentNode;
      irmaoOrigem = card.nextElementSibling;
      ordemAntes = idsDe(origem).join(',');
      soltou = false;
      card.classList.add('is-dragging');
      try {
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', card.dataset.id);
        /* Pegar pelo titulo arrastaria a ancora; a imagem tem que ser o card. */
        if (e.dataTransfer.setDragImage) { e.dataTransfer.setDragImage(card, 20, 20); }
      } catch (err) { /* alguns browsers reclamam do setData: o arrasto segue */ }
    });

    board.addEventListener('dragend', function () {
      if (arrastando) {
        arrastando.classList.remove('is-dragging');
        /* Esc, ou soltar fora de qualquer coluna: o dragover ja mexeu no DOM
           e nenhum drop vai acontecer. Sem isto o card fica na coluna errada
           sem nunca ter sido salvo. */
        if (!soltou && origem) { desfaz(arrastando, origem, irmaoOrigem); }
      }
      [].forEach.call(board.querySelectorAll('.board-drop'), function (d) {
        d.classList.remove('is-over');
      });
      arrastando = null;
    });

    board.addEventListener('dragover', function (e) {
      if (!arrastando) { return; }
      var drop = e.target.closest && e.target.closest('.board-drop');
      if (!drop) { return; }
      e.preventDefault();                  // sem isso o browser recusa o drop
      e.dataTransfer.dropEffect = 'move';
      drop.classList.add('is-over');
      var ref = alvoDepois(drop, e.clientY);
      if (ref) { drop.insertBefore(arrastando, ref); } else { drop.appendChild(arrastando); }
    });

    board.addEventListener('dragleave', function (e) {
      var drop = e.target.closest && e.target.closest('.board-drop');
      if (drop && !drop.contains(e.relatedTarget)) { drop.classList.remove('is-over'); }
    });

    board.addEventListener('drop', function (e) {
      var drop = e.target.closest && e.target.closest('.board-drop');
      if (!drop || !arrastando) { return; }
      e.preventDefault();
      drop.classList.remove('is-over');
      soltou = true;

      var card = arrastando;
      var dePai = origem;
      var deIrmao = irmaoOrigem;
      var ids = idsDe(drop);

      /* Soltou exatamente onde estava: nada mudou, nao gasta request. */
      if (drop === dePai && ids.join(',') === ordemAntes) { contagem(); return; }

      var corpo = new URLSearchParams();
      corpo.set('csrf', board.dataset.csrf || '');
      corpo.set('task_id', card.dataset.id);
      corpo.set('column_id', drop.dataset.col);
      corpo.set('ordem', ids.join(','));

      card.classList.add('is-saving');
      contagem();
      fetch('board.php?r=move', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
        body: corpo.toString(),
        credentials: 'same-origin'
      }).then(function (r) {
        /* Sessao expirada devolve HTML, nao JSON: nao deixar o parse estourar. */
        return r.json().catch(function () {
          return { error: r.status === 400 ? 'Sessão expirada — recarregue a página.' : 'Resposta inesperada do servidor.' };
        });
      }).then(function (data) {
        card.classList.remove('is-saving');
        if (data && data.ok) { return; }
        throw new Error((data && data.error) || 'Não deu para mover.');
      }).catch(function (err) {
        card.classList.remove('is-saving');
        desfaz(card, dePai, deIrmao);
        window.alert(err.message || 'Não deu para mover. Recarregue a página.');
      });
    });

    contagem();
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
