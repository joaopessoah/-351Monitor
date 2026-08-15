/* +351 Monitor — interações da home */
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
      if (e.target.closest('a')) {
        burger.setAttribute('aria-expanded', 'false');
        burger.setAttribute('aria-label', 'Abrir menu');
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

  /* ---------- Gráfico do mockup: desenho da linha ---------- */
  var chartLine = document.querySelector('.shot-chart .line');
  if (chartLine && !reduceMotion && 'IntersectionObserver' in window) {
    var len = chartLine.getTotalLength();
    chartLine.style.strokeDasharray = String(len);
    chartLine.style.strokeDashoffset = String(len);
    var cio = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          chartLine.getBoundingClientRect(); /* força reflow antes da transição */
          chartLine.style.transition = 'stroke-dashoffset 1800ms cubic-bezier(.4,0,.2,1) 350ms';
          chartLine.style.strokeDashoffset = '0';
          cio.disconnect();
        }
      });
    }, { threshold: 0.35 });
    cio.observe(chartLine.closest('.shot'));
  }

  /* ---------- Calculadora: impacto financeiro potencial da improdutividade ---------- */
  var form = document.getElementById('calc');
  if (form) {
    var inColab = document.getElementById('calc-colab');
    var inCusto = document.getElementById('calc-custo');
    var inDias = document.getElementById('calc-dias');
    var inJornada = document.getElementById('calc-jornada');

    var premBtn = document.getElementById('calc-premissas-btn');
    var premCampos = document.getElementById('calc-premissas-campos');
    var premDias = document.getElementById('calc-prem-dias');
    var premHoras = document.getElementById('calc-prem-horas');

    var valueWrap = document.getElementById('calc-valor');
    var outContext = document.getElementById('calc-context');
    var outMensal = document.getElementById('calc-mensal');
    var outExplain = document.getElementById('calc-explain');
    var outAnual = document.getElementById('calc-anual');
    var opHoras = document.getElementById('calc-op-horas');
    var opJornadas = document.getElementById('calc-op-jornadas');
    var outSr = document.getElementById('calc-sr');
    var demoLink = document.getElementById('calc-demo');

    var cenarios = [
      { pct: 0.10, mes: document.getElementById('cen-10-mes'), ano: document.getElementById('cen-10-ano') },
      { pct: 0.25, mes: document.getElementById('cen-25-mes'), ano: document.getElementById('cen-25-ano') },
      { pct: 0.50, mes: document.getElementById('cen-50-mes'), ano: document.getElementById('cen-50-ano') }
    ];
    var campos = {
      colab:   { input: inColab,   wrap: document.getElementById('wrap-colab'),   erro: document.getElementById('err-colab') },
      custo:   { input: inCusto,   wrap: document.getElementById('wrap-custo'),   erro: document.getElementById('err-custo') },
      dias:    { input: inDias,    wrap: document.getElementById('wrap-dias'),    erro: document.getElementById('err-dias') },
      jornada: { input: inJornada, wrap: document.getElementById('wrap-jornada'), erro: document.getElementById('err-jornada') }
    };

    var fmt = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });
    var NBSP = '\u00A0';
    function brl(v) { return 'R$' + NBSP + fmt.format(Math.round(v)); }
    function plural(n, um, muitos) { return n === 1 ? um : muitos; }

    /* Eventos de conversao: entrega no dataLayer (GTM) ou gtag quando existirem; sem analytics
       instalado e no-op. Nomes prontos para cruzar interacao na calculadora com conversao. */
    var trackCalc = function (eventName, params) {
      if (window.dataLayer && typeof window.dataLayer.push === 'function') {
        var payload = { event: eventName };
        for (var key in params) {
          if (Object.prototype.hasOwnProperty.call(params, key)) { payload[key] = params[key]; }
        }
        window.dataLayer.push(payload);
      } else if (typeof window.gtag === 'function') {
        window.gtag('event', eventName, params || {});
      }
    };

    /* Máscara monetária brasileira: o campo guarda só os dígitos formatados; o "R$" fica no prefixo visual. */
    function digitos(str) { return String(str).replace(/\D+/g, '').slice(0, 9); }
    function mascararCusto() {
      var d = digitos(inCusto.value);
      inCusto.value = d ? fmt.format(parseInt(d, 10)) : '';
    }

    function lerCampos() {
      var d = digitos(inCusto.value);
      return {
        colab: parseInt(inColab.value, 10),
        custo: d ? parseInt(d, 10) : NaN,
        dias: parseInt(inDias.value, 10),
        jornada: parseInt(inJornada.value, 10),
        minutos: parseInt((form.querySelector('input[name="minutos"]:checked') || {}).value, 10) || 30
      };
    }

    function setErro(nome, msg) {
      var campo = campos[nome];
      if (msg) {
        campo.erro.textContent = msg;
        campo.erro.hidden = false;
        campo.wrap.classList.add('invalid');
      } else {
        campo.erro.hidden = true;
        campo.wrap.classList.remove('invalid');
      }
    }

    var REGRAS = [
      { nome: 'colab',   min: 1, max: 100000,    vazio: 'Informe o número de colaboradores para continuar.', faixa: 'Use um número de colaboradores entre 1 e 100.000.' },
      { nome: 'custo',   min: 1, max: 999999999, vazio: 'Informe o custo médio mensal para continuar.',      faixa: 'Informe um custo médio mensal maior que zero.' },
      { nome: 'dias',    min: 1, max: 31,        vazio: 'Informe os dias úteis para continuar.',             faixa: 'Use entre 1 e 31 dias úteis por mês.' },
      { nome: 'jornada', min: 1, max: 24,        vazio: 'Informe as horas de jornada para continuar.',       faixa: 'Use entre 1 e 24 horas de jornada por dia.' }
    ];

    /* mostrar=true exibe as mensagens integradas ao layout e foca o primeiro campo inválido;
       mostrar=false apenas responde se está tudo válido (recalculo silencioso). */
    function validar(c, mostrar) {
      var primeiroInvalido = null;
      REGRAS.forEach(function (r) {
        var v = c[r.nome];
        var msg = null;
        if (isNaN(v)) { msg = r.vazio; }
        else if (v < r.min || v > r.max) { msg = r.faixa; }
        if (mostrar) { setErro(r.nome, msg); }
        if (msg && !primeiroInvalido) { primeiroInvalido = campos[r.nome].input; }
      });
      if (mostrar && primeiroInvalido) { primeiroInvalido.focus(); }
      return !primeiroInvalido;
    }

    /* Matemática da simulação:
       custo por hora  = custo mensal ÷ (dias úteis × jornada diária)
       horas por mês   = colaboradores × (minutos ÷ 60) × dias úteis
       impacto mensal  = horas por mês × custo por hora
                       = colaboradores × custo mensal × (minutos ÷ 60) ÷ jornada diária */
    function simular(c) {
      var horasMes = c.colab * (c.minutos / 60) * c.dias;
      var mensal = c.colab * c.custo * (c.minutos / 60) / c.jornada;
      return { horasMes: horasMes, jornadas: Math.floor(horasMes / c.jornada), mensal: mensal, anual: mensal * 12 };
    }

    var shownMensal = 62500; /* espelho do valor ja impresso no HTML inicial */
    var countRaf = 0;

    /* Contagem crescente curta (~650ms) ate o alvo; com prefers-reduced-motion vai direto. */
    function mostrarMensal(target, doZero) {
      target = Math.round(target);
      cancelAnimationFrame(countRaf);
      if (reduceMotion) {
        shownMensal = target;
        outMensal.textContent = brl(target);
        return;
      }
      var from = doZero ? 0 : shownMensal;
      if (from === target) {
        shownMensal = target;
        outMensal.textContent = brl(target);
        return;
      }
      var dur = 650;
      var t0 = performance.now();
      var step = function (t) {
        var p = Math.min(1, (t - t0) / dur);
        var eased = 1 - Math.pow(1 - p, 3); /* easeOutCubic: acelera no inicio, pousa suave */
        shownMensal = Math.round(from + (target - from) * eased);
        outMensal.textContent = brl(shownMensal);
        if (p < 1) { countRaf = requestAnimationFrame(step); }
      };
      countRaf = requestAnimationFrame(step);
    }

    function render(c, r, opts) {
      var horas = Math.round(r.horasMes);

      outContext.textContent = c.minutos + ' minutos por dia podem parecer pouco.';
      outExplain.textContent = 'Esse seria o impacto financeiro potencial se ' + c.minutos +
        ' minutos da jornada diária ' +
        (c.colab === 1 ? 'do colaborador analisado' : 'dos ' + fmt.format(c.colab) + ' colaboradores analisados') +
        ' não estivessem gerando produtividade.';
      outAnual.textContent = brl(r.anual);

      opHoras.innerHTML = '<b>' + fmt.format(horas) + '</b> ' + plural(horas, 'hora', 'horas') + '/mês';
      opJornadas.hidden = r.jornadas < 1;
      if (r.jornadas >= 1) {
        opJornadas.innerHTML = '≈ <b>' + fmt.format(r.jornadas) + '</b> ' +
          plural(r.jornadas, 'jornada', 'jornadas') + ' de trabalho de ' + c.jornada + ' ' +
          plural(c.jornada, 'hora', 'horas');
      }

      cenarios.forEach(function (cen) {
        var mes = r.mensal * cen.pct;
        cen.mes.textContent = brl(mes) + '/mês';
        cen.ano.textContent = brl(mes * 12) + '/ano';
      });

      premDias.textContent = c.dias + ' ' + plural(c.dias, 'dia útil', 'dias úteis') + '/mês';
      premHoras.textContent = c.jornada + ' ' + plural(c.jornada, 'hora', 'horas') + '/dia';

      /* leitor de tela recebe UMA frase com os valores finais (a contagem visual e aria-hidden) */
      outSr.textContent = 'Impacto financeiro potencial de ' + brl(r.mensal).replace(NBSP, ' ') +
        ' por mês e ' + brl(r.anual).replace(NBSP, ' ') + ' por ano, correspondente a ' +
        fmt.format(horas) + ' ' + plural(horas, 'hora', 'horas') + ' por mês.';

      mostrarMensal(r.mensal, !!(opts && opts.doZero));
      if (opts && opts.pop && !reduceMotion) {
        valueWrap.classList.remove('pop');
        void valueWrap.offsetWidth; /* reinicia a animação */
        valueWrap.classList.add('pop');
      }
    }

    function calcular(opts) {
      var c = lerCampos();
      if (!validar(c, false)) { return null; }
      var r = simular(c);
      render(c, r, opts);
      return { c: c, r: r };
    }

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var c = lerCampos();
      if (!validar(c, true)) { return; }
      var r = simular(c);
      render(c, r, { doZero: true, pop: true });
      trackCalc('calculator_calculate', {
        colaboradores: c.colab,
        custo_mensal: c.custo,
        minutos: c.minutos,
        dias_uteis: c.dias,
        jornada: c.jornada,
        impacto_mensal: Math.round(r.mensal),
        impacto_anual: Math.round(r.anual),
        horas_mes: Math.round(r.horasMes)
      });
    });

    inCusto.addEventListener('input', mascararCusto);
    form.addEventListener('input', function (e) {
      for (var nome in campos) {
        if (Object.prototype.hasOwnProperty.call(campos, nome) && campos[nome].input === e.target) {
          setErro(nome, null);
        }
      }
      calcular();
    });

    inColab.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'colaboradores', valor: parseInt(inColab.value, 10) || 0 });
    });
    inCusto.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'custo_mensal', valor: parseInt(digitos(inCusto.value) || '0', 10) });
    });
    inDias.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'dias_uteis', valor: parseInt(inDias.value, 10) || 0 });
    });
    inJornada.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'jornada', valor: parseInt(inJornada.value, 10) || 0 });
    });
    form.addEventListener('change', function (e) {
      if (e.target && e.target.name === 'minutos') {
        trackCalc('calculator_interaction', { campo: 'periodo', valor: parseInt(e.target.value, 10) });
      }
    });

    if (premBtn && premCampos) {
      premBtn.addEventListener('click', function () {
        var abrir = premCampos.hidden;
        premCampos.hidden = !abrir;
        premBtn.setAttribute('aria-expanded', String(abrir));
        premBtn.textContent = abrir ? 'Ocultar premissas' : 'Alterar premissas';
        if (abrir) { inDias.focus(); }
        trackCalc('calculator_interaction', { campo: 'premissas', valor: abrir ? 1 : 0 });
      });
    }

    if (demoLink) {
      demoLink.addEventListener('click', function () {
        var c = lerCampos();
        var params = {};
        if (validar(c, false)) {
          var r = simular(c);
          params.impacto_mensal = Math.round(r.mensal);
          params.impacto_anual = Math.round(r.anual);
          params.horas_mes = Math.round(r.horasMes);
        }
        trackCalc('calculator_demo_click', params);
      });
    }

    /* Efeito de descoberta: na primeira vez que a calculadora entra na tela, o impacto conta de 0 ao valor. */
    if ('IntersectionObserver' in window && !reduceMotion) {
      var calcIo = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            calcular({ doZero: true });
            calcIo.disconnect();
          }
        });
      }, { threshold: 0.15 });
      calcIo.observe(form);
    } else {
      calcular();
    }
  }

  /* ---------- Ano no rodapé ---------- */
  var ano = document.getElementById('ano');
  if (ano) { ano.textContent = String(new Date().getFullYear()); }
})();
