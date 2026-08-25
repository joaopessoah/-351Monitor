/* Exercita o site/assets/js/track.js real num DOM mínimo: o que ele manda para
   o collect.php, e o código da visita que ele pendura nos links do WhatsApp. */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', '..', 'site', 'assets', 'js', 'track.js'), 'utf8');

let falhas = 0, total = 0;
const check = (cond, msg) => { total++; if (!cond) { falhas++; console.log('  FALHOU: ' + msg); } };
const igual = (obtido, esperado, msg) => check(
  JSON.stringify(obtido) === JSON.stringify(esperado),
  msg + ' — esperava ' + JSON.stringify(esperado) + ', veio ' + JSON.stringify(obtido)
);

/* ---------- DOM mínimo ---------- */
function montar(opts = {}) {
  const posts = [];
  const listeners = { document: {}, window: {} };

  const links = (opts.links || []).map(href => ({
    _href: href,
    textContent: (opts.textos && opts.textos[href]) || 'Agendar demonstração',
    getAttribute(k) { return k === 'href' ? this._href : null; },
    setAttribute(k, v) { if (k === 'href') { this._href = v; } }
  }));

  const doc = {
    visibilityState: 'visible',
    title: 'Título da página',
    referrer: opts.referrer || '',
    documentElement: { scrollHeight: 4000 },
    body: { scrollHeight: 4000 },
    addEventListener(ev, fn) { (listeners.document[ev] = listeners.document[ev] || []).push(fn); },
    querySelectorAll() { return links; }
  };

  const win = {
    scrollY: 0,
    innerHeight: 900,
    screen: { width: 1920 },
    addEventListener(ev, fn) { (listeners.window[ev] = listeners.window[ev] || []).push(fn); }
  };

  const nav = {
    globalPrivacyControl: opts.gpc === true ? true : undefined,
    sendBeacon: opts.semBeacon ? undefined : (url, blob) => {
      posts.push({ via: 'beacon', body: JSON.parse(blob._texto) });
      return true;
    }
  };

  const loc = {
    pathname: opts.pathname || '/',
    search: opts.search || '',
    host: 'www.mais351monitor.com.br',
    href: 'https://www.mais351monitor.com.br/'
  };

  function Blob(partes) { this._texto = partes.join(''); }

  const fetchFake = (url, init) => {
    posts.push({ via: 'fetch', body: JSON.parse(init.body), tipo: init.headers['Content-Type'], keepalive: init.keepalive });
    return Promise.resolve({ json: () => Promise.resolve(opts.resposta || { ok: true, ref: 'K7M2Q9' }) });
  };

  const ctx = {
    navigator: nav, document: doc, location: loc, Blob, JSON, Math, Date, Promise,
    isFinite, parseInt, String, console
  };
  ctx.window = win;
  win.fetch = fetchFake;
  ctx.fetch = fetchFake;
  vm.createContext(ctx);
  vm.runInContext(SRC, ctx);

  return { posts, listeners, links, win, doc, ctx, esperar: () => new Promise(r => setImmediate(() => setImmediate(r))) };
}

/* Clique como o listener de captura em document veria. */
function clicar(m, link) {
  const alvo = { closest: sel => (sel === 'a[href]' ? link : null) };
  m.listeners.document.click.forEach(fn => fn({ target: alvo }));
}

(async function () {
  console.log('== pageview ==');
  {
    const m = montar({
      pathname: '/', search: '?utm_source=google&utm_medium=cpc',
      referrer: 'https://www.google.com/search?q=produtividade'
    });
    const pv = m.posts[0];
    check(pv && pv.via === 'fetch', 'o pageview precisa ir por fetch para conseguir ler o ref da resposta');
    igual(pv.body.t, 'pv', 'tipo da batida');
    igual(pv.body.p, '/?utm_source=google&utm_medium=cpc', 'o path tem que levar a query (o servidor extrai a UTM)');
    igual(pv.body.r, 'https://www.google.com/search?q=produtividade', 'referrer');
    igual(pv.body.sw, 1920, 'largura da tela');
    igual(pv.tipo, 'text/plain;charset=UTF-8', 'content-type tem que ser simples, senão o navegador faz preflight');
    check(pv.keepalive === true, 'sem keepalive a batida morre quando a aba fecha');
  }

  console.log('== código da visita nos links do WhatsApp ==');
  {
    const comTexto = 'https://wa.me/5511925690601?text=Ol%C3%A1!%20Quero%20agendar%20uma%20demonstra%C3%A7%C3%A3o%20do%20%2B351%20Monitor.';
    const m = montar({
      links: [comTexto, 'https://wa.me/5511925690601', 'mailto:contato@mais351monitor.com.br',
              'https://www.linkedin.com/company/351-monitor/']
    });
    await m.esperar();
    const texto = decodeURIComponent(/[?&]text=([^&]*)/.exec(m.links[0]._href)[1]);
    igual(texto, 'Olá! Quero agendar uma demonstração do +351 Monitor. #K7M2Q9',
      'a mensagem que a pessoa vê no WhatsApp');
    igual(m.links[1]._href, 'https://wa.me/5511925690601?text=%23K7M2Q9',
      'link sem texto pré-preenchido');
    igual(m.links[2]._href, 'mailto:contato@mais351monitor.com.br', 'mailto não pode ser tocado');
    igual(m.links[3]._href, 'https://www.linkedin.com/company/351-monitor/', 'link externo não pode ser tocado');
  }

  console.log('== cliques ==');
  {
    const wa = 'https://wa.me/5511925690601?text=Ol%C3%A1!%20Quero%20falar%20com%20um%20especialista';
    const m = montar({
      links: [wa, 'mailto:contato@mais351monitor.com.br', '#produto', 'https://demo.mais351monitor.com.br', 'privacidade.html'],
      textos: {
        [wa]: '  Falar com um\n  especialista  ',
        'mailto:contato@mais351monitor.com.br': 'contato@mais351monitor.com.br',
        '#produto': 'O produto',
        'https://demo.mais351monitor.com.br': 'Ver demo',
        'privacidade.html': 'Privacidade'
      }
    });
    await m.esperar();
    const antes = m.posts.length;
    m.links.forEach(l => clicar(m, l));
    const evs = m.posts.slice(antes).map(p => p.body);

    igual(evs.length, 4, 'link interno relativo não pode virar evento (já vem como pageview)');
    igual([evs[0].n, evs[0].l], ['whatsapp', 'Falar com um especialista'], 'clique no WhatsApp, com o rótulo normalizado');
    igual(evs[0].tg, 'Olá! Quero falar com um especialista',
      'o alvo é o texto pré-preenchido SEM o código da visita (senão cada visita vira um grupo no painel)');
    igual([evs[1].n, evs[1].tg], ['email', 'contato@mais351monitor.com.br'], 'clique no mailto');
    igual([evs[2].n, evs[2].tg], ['anchor', '#produto'], 'clique em âncora interna');
    igual([evs[3].n, evs[3].tg], ['outbound', 'https://demo.mais351monitor.com.br'], 'clique para outro domínio');
    igual(m.posts[antes].via, 'beacon', 'clique tem que sair por sendBeacon, senão some na navegação');
  }

  console.log('== bridge do dataLayer (os trackCalc do home.js) ==');
  {
    const m = montar({});
    await m.esperar();
    const antes = m.posts.length;
    const dl = m.ctx.window.dataLayer;

    check(Array.isArray(dl), 'o dataLayer tem que continuar um array de verdade, para um GTM futuro funcionar');
    dl.push({ event: 'calculator_interaction', campo: 'colaboradores', valor: 45 });
    dl.push({ event: 'calculator_interaction', campo: 'colaboradores', valor: 45 });
    dl.push({ event: 'calculator_interaction', campo: 'colaboradores', valor: 80 });
    dl.push({ event: 'calculator_calculate', colaboradores: 80, impacto_mensal: 68000 });
    dl.push({ event: 'calculator_demo_click', impacto_mensal: 68000 });
    dl.push({ naoTemEvent: 1 });

    const evs = m.posts.slice(antes).map(p => p.body);
    igual(evs.length, 4, 'o mesmo valor repetido no mesmo campo não pode virar evento novo');
    igual([evs[0].n, evs[0].l, evs[0].v], ['calculator_interaction', 'colaboradores', 45], 'interação na calculadora');
    igual(evs[1].v, 80, 'valor novo no mesmo campo conta');
    igual([evs[2].n, evs[2].l, evs[2].v], ['calculator_calculate', '80 colaboradores', 68000], 'cálculo do impacto');
    igual([evs[3].n, evs[3].v], ['calculator_demo_click', 68000], 'CTA depois do cálculo');
    igual(dl.length, 6, 'o array precisa guardar todos os pushes, inclusive os que ignoramos');
  }

  console.log('== tempo de leitura e scroll ==');
  {
    const m = montar({});
    await m.esperar();
    const antes = m.posts.length;
    m.win.scrollY = 3100; // 3100 + 900 = 4000 de 4000
    m.listeners.window.scroll.forEach(fn => fn());
    m.doc.visibilityState = 'hidden';
    m.listeners.document.visibilitychange.forEach(fn => fn());

    const fim = m.posts.slice(antes).map(p => p.body).filter(b => b.t === 'end');
    igual(fim.length, 1, 'sair da aba manda exatamente um end');
    igual(fim[0].sc, 100, 'scroll máximo');
    check(typeof fim[0].s === 'number', 'os segundos têm que ir como número');

    const antes2 = m.posts.length;
    m.doc.visibilityState = 'visible';
    m.listeners.document.visibilitychange.forEach(fn => fn());
    m.doc.visibilityState = 'hidden';
    m.listeners.document.visibilitychange.forEach(fn => fn());
    igual(m.posts.slice(antes2).filter(p => p.body.t === 'end').length, 0,
      'trocar de aba de novo sem tempo novo não pode gerar batida repetida');
  }

  console.log('== Global Privacy Control ==');
  {
    const m = montar({ gpc: true });
    await m.esperar();
    igual(m.posts.length, 0, 'com GPC ligado nada pode ser enviado');
    check(m.listeners.document.click === undefined, 'com GPC ligado nem o listener de clique pode ser instalado');
  }

  console.log('== navegador sem sendBeacon ==');
  {
    const m = montar({ semBeacon: true, links: ['https://wa.me/5511925690601'] });
    await m.esperar();
    const antes = m.posts.length;
    clicar(m, m.links[0]);
    igual(m.posts[antes].via, 'fetch', 'sem sendBeacon o clique tem que cair no fetch');
  }

  console.log('');
  console.log(falhas === 0 ? `TODOS OS ${total} TESTES PASSARAM` : `${falhas} FALHAS de ${total}`);
  process.exit(falhas === 0 ? 0 : 1);
})();
