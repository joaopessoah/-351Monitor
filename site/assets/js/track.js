/* +351 Monitor — analytics próprio do site.
   Manda views e cliques para /crm/collect.php (mesmo domínio, mesmo banco do CRM).
   Sem cookie, sem localStorage, sem terceiros: quem identifica a visita é o
   servidor, por um hash diário que não sobrevive à virada do dia.

   Precisa carregar ANTES do home.js: o bridge de dataLayer aqui embaixo é o que
   captura os trackCalc() da calculadora sem tocar naquele arquivo. */
(function () {
  'use strict';

  var ENDPOINT = '/crm/collect.php';
  var MAX_EVENTOS = 60; /* teto por aba, para um bug de laço nunca virar flood */

  /* Global Privacy Control: opt-out explícito do visitante. Respeita e não roda. */
  if (navigator.globalPrivacyControl === true) { return; }

  var path = location.pathname + location.search;
  var ref = null;
  var enviados = 0;
  var maxScroll = 0;
  var vistos = {};

  /* Tempo engajado: só conta enquanto a aba está visível. */
  var acumulado = 0;
  var desde = document.visibilityState === 'visible' ? Date.now() : 0;
  var ultimoFim = -1;

  function pausar() { if (desde) { acumulado += Date.now() - desde; desde = 0; } }
  function retomar() { if (!desde) { desde = Date.now(); } }
  function segundos() {
    return Math.min(7200, Math.round((acumulado + (desde ? Date.now() - desde : 0)) / 1000));
  }

  function num(v) {
    var n = parseInt(v, 10);
    return isFinite(n) ? n : null;
  }

  function post(dados, querResposta) {
    var corpo = JSON.stringify(dados);
    if (!querResposta && navigator.sendBeacon) {
      try {
        if (navigator.sendBeacon(ENDPOINT, new Blob([corpo], { type: 'text/plain;charset=UTF-8' }))) { return; }
      } catch (e) { /* segue no fetch */ }
    }
    if (!window.fetch) { return; }
    fetch(ENDPOINT, {
      method: 'POST',
      body: corpo,
      keepalive: true,
      credentials: 'omit',
      headers: { 'Content-Type': 'text/plain;charset=UTF-8' }
    }).then(function (r) {
      return querResposta ? r.json() : null;
    }).then(function (j) {
      if (j && j.ref) { aplicarRef(j.ref); }
    })['catch'](function () { /* analytics nunca quebra a página */ });
  }

  function evento(nome, rotulo, alvo, valor) {
    if (enviados >= MAX_EVENTOS) { return; }
    enviados++;
    post({ t: 'ev', p: path, n: nome, l: rotulo || null, tg: alvo || null, v: num(valor) });
  }

  /* ---------- Código da visita nos links do WhatsApp ----------
     O servidor devolve um código curto por visita; ele entra no fim do texto
     pré-preenchido ("… #K7M2Q9"). Quando a conversa chega, esse código liga o
     lead à jornada que a pessoa fez no site. */
  function comRef(href, codigo) {
    var corte = href.indexOf('?');
    var base = corte === -1 ? href : href.slice(0, corte);
    var atual = '';
    var outros = [];
    if (corte !== -1) {
      href.slice(corte + 1).split('&').forEach(function (par) {
        if (!par) { return; }
        if (par.indexOf('text=') === 0) {
          try { atual = decodeURIComponent(par.slice(5).replace(/\+/g, ' ')); } catch (e) { atual = ''; }
        } else {
          outros.push(par);
        }
      });
    }
    outros.push('text=' + encodeURIComponent((atual ? atual + ' ' : '') + '#' + codigo));
    return base + '?' + outros.join('&');
  }

  function ehWhats(href) {
    return href.indexOf('wa.me/') !== -1 || href.indexOf('api.whatsapp.com') !== -1;
  }

  function aplicarRef(codigo) {
    if (ref === codigo) { return; }
    ref = codigo;
    var links = document.querySelectorAll('a[href]');
    for (var i = 0; i < links.length; i++) {
      var href = links[i].getAttribute('href') || '';
      if (ehWhats(href) && href.indexOf('%23' + codigo) === -1) {
        links[i].setAttribute('href', comRef(href, codigo));
      }
    }
  }

  /* Texto pré-preenchido do wa.me: é ele que diz de qual CTA a pessoa saiu. */
  function ctaDoWhats(href) {
    var m = /[?&]text=([^&]*)/.exec(href);
    if (!m) { return null; }
    var texto;
    try { texto = decodeURIComponent(m[1].replace(/\+/g, ' ')); } catch (e) { return null; }
    /* Tira o código da visita do fim: ele já está gravado na própria visita, e
       sem ele o texto vira a identidade estável da CTA no painel. */
    if (ref) {
      if (texto === '#' + ref) { return null; }
      if (texto.slice(-(ref.length + 2)) === ' #' + ref) { texto = texto.slice(0, -(ref.length + 2)); }
    }
    return texto || null;
  }

  /* ---------- Cliques ----------
     Fase de captura: roda antes de qualquer handler da página. */
  document.addEventListener('click', function (e) {
    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
    if (!a) { return; }
    var href = a.getAttribute('href') || '';
    var texto = (a.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 120);

    if (ehWhats(href)) {
      evento('whatsapp', texto, ctaDoWhats(href));
    } else if (href.indexOf('mailto:') === 0) {
      evento('email', texto, href.slice(7));
    } else if (href.charAt(0) === '#' && href.length > 1) {
      evento('anchor', texto, href);
    } else if (/^https?:\/\//i.test(href) && href.indexOf('//' + location.host) === -1) {
      evento('outbound', texto, href);
    }
  }, true);

  /* ---------- Bridge do dataLayer ----------
     O home.js já chama trackCalc() em 8 pontos da calculadora e entrega no
     dataLayer "quando existir". Existindo este, os eventos vêm para cá.
     O array continua sendo um dataLayer de verdade, então plugar um GTM
     por cima no futuro não quebra nada. */
  var dl = window.dataLayer = window.dataLayer || [];
  var pushNativo = dl.push.bind(dl);
  dl.push = function (o) {
    try { daCalculadora(o); } catch (e) { /* ignora */ }
    return pushNativo.apply(null, arguments);
  };

  function daCalculadora(o) {
    if (!o || typeof o.event !== 'string') { return; }
    var nome = o.event.toLowerCase().replace(/[^a-z0-9_]/g, '_').slice(0, 48);
    if (nome === 'calculator_interaction') {
      /* Só a última resposta de cada campo interessa — o input dispara muito. */
      var chave = String(o.campo);
      if (vistos[chave] === o.valor) { return; }
      vistos[chave] = o.valor;
      evento(nome, chave, null, o.valor);
    } else if (nome === 'calculator_calculate') {
      evento(nome, o.colaboradores ? o.colaboradores + ' colaboradores' : null, null, o.impacto_mensal);
    } else if (nome === 'calculator_demo_click') {
      evento(nome, null, null, o.impacto_mensal);
    } else if (nome.length >= 2) {
      evento(nome, null, null, o.valor);
    }
  }

  /* ---------- Scroll e tempo de leitura ---------- */
  function medirScroll() {
    var doc = document.documentElement;
    var altura = Math.max(doc.scrollHeight || 0, document.body ? document.body.scrollHeight : 0);
    if (altura <= 0) { return; }
    var pct = Math.round(((window.scrollY || 0) + window.innerHeight) / altura * 100);
    if (pct > maxScroll) { maxScroll = Math.min(100, pct); }
  }
  window.addEventListener('scroll', medirScroll, { passive: true });

  function fim() {
    medirScroll();
    var s = segundos();
    if (s === ultimoFim) { return; } /* nada mudou desde o último envio */
    ultimoFim = s;
    post({ t: 'end', p: path, s: s, sc: maxScroll });
  }

  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') { pausar(); fim(); } else { retomar(); }
  });
  window.addEventListener('pagehide', function () { pausar(); fim(); });

  /* ---------- Pageview ---------- */
  post({
    t: 'pv',
    p: path,
    ti: (document.title || '').slice(0, 160),
    r: document.referrer || null,
    sw: window.screen ? window.screen.width : null
  }, true);

  medirScroll();
})();
