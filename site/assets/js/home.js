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

  /* ---------- Calculadora: impacto do tempo na escala da operacao ---------- */
  var form = document.getElementById('calc');
  if (form) {
    var inColab = document.getElementById('calc-colab');
    var inDias = document.getElementById('calc-dias');
    var valueWrap = document.getElementById('calc-valor');
    var outHoras = document.getElementById('calc-horas');
    var outContext = document.getElementById('calc-context');
    var outJornadas = document.getElementById('calc-jornadas');
    var outConta = document.getElementById('calc-conta');
    var outSr = document.getElementById('calc-sr');
    var titleMinutos = document.getElementById('calc-title-minutos');
    var demoLink = document.getElementById('calc-demo');
    var fmt = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

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

    function clamp(v, min, max, fallback) {
      v = parseInt(v, 10);
      if (isNaN(v)) { return fallback; }
      return Math.min(max, Math.max(min, v));
    }

    function lerCampos() {
      var colab = clamp(inColab.value, 1, 100000, 100);
      var dias = clamp(inDias.value, 1, 31, 22);
      var minutos = parseInt((form.querySelector('input[name="minutos"]:checked') || {}).value, 10) || 30;
      return { colab: colab, dias: dias, minutos: minutos, horas: Math.round(colab * minutos / 60 * dias) };
    }

    var shownHoras = 1100; /* espelho do valor ja impresso no HTML inicial */
    var countRaf = 0;

    /* Contagem crescente curta (~650ms) ate o alvo; com prefers-reduced-motion vai direto. */
    function mostrarHoras(target, doZero) {
      cancelAnimationFrame(countRaf);
      if (reduceMotion) {
        shownHoras = target;
        outHoras.textContent = fmt.format(target);
        return;
      }
      var from = doZero ? 0 : shownHoras;
      if (from === target) {
        shownHoras = target;
        outHoras.textContent = fmt.format(target);
        return;
      }
      var dur = 650;
      var t0 = performance.now();
      var step = function (t) {
        var p = Math.min(1, (t - t0) / dur);
        var eased = 1 - Math.pow(1 - p, 3); /* easeOutCubic: acelera no inicio, pousa suave */
        shownHoras = Math.round(from + (target - from) * eased);
        outHoras.textContent = fmt.format(shownHoras);
        if (p < 1) { countRaf = requestAnimationFrame(step); }
      };
      countRaf = requestAnimationFrame(step);
    }

    function calcular(opts) {
      var c = lerCampos();
      var jornadas = Math.floor(c.horas / 8);

      titleMinutos.textContent = c.minutos + ' minutos';
      outContext.textContent = c.minutos + ' minutos/dia × ' + fmt.format(c.colab) + ' colaboradores';
      outConta.textContent = fmt.format(c.colab) + ' colaboradores × ' + c.minutos + ' minutos × ' +
        c.dias + ' dias = ' + fmt.format(c.horas) + ' horas/mês';

      outJornadas.hidden = jornadas < 1;
      if (jornadas >= 1) {
        outJornadas.innerHTML = 'O equivalente a aproximadamente <b>' + fmt.format(jornadas) + '</b> ' +
          (jornadas === 1 ? 'jornada' : 'jornadas') + ' de trabalho de 8 horas.';
      }

      /* leitor de tela recebe UMA frase com o valor final (a contagem visual e aria-hidden) */
      outSr.textContent = fmt.format(c.horas) + ' horas por mês na sua operação' +
        (jornadas >= 1
          ? ', o equivalente a aproximadamente ' + fmt.format(jornadas) +
            (jornadas === 1 ? ' jornada' : ' jornadas') + ' de trabalho de 8 horas.'
          : '.');

      mostrarHoras(c.horas, !!(opts && opts.doZero));
      if (opts && opts.pop && !reduceMotion) {
        valueWrap.classList.remove('pop');
        void valueWrap.offsetWidth; /* reinicia a animação */
        valueWrap.classList.add('pop');
      }
      return c;
    }

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var c = calcular({ doZero: true, pop: true });
      trackCalc('calculator_calculate',
        { colaboradores: c.colab, minutos: c.minutos, dias_uteis: c.dias, horas_mes: c.horas });
    });
    form.addEventListener('input', function () { calcular(); });

    inColab.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'colaboradores', valor: clamp(inColab.value, 1, 100000, 100) });
    });
    inDias.addEventListener('change', function () {
      trackCalc('calculator_interaction', { campo: 'dias_uteis', valor: clamp(inDias.value, 1, 31, 22) });
    });
    form.addEventListener('change', function (e) {
      if (e.target && e.target.name === 'minutos') {
        trackCalc('calculator_interaction', { campo: 'periodo', valor: parseInt(e.target.value, 10) });
      }
    });
    if (demoLink) {
      demoLink.addEventListener('click', function () {
        trackCalc('calculator_demo_click', { horas_mes: lerCampos().horas });
      });
    }

    /* Efeito de descoberta: na primeira vez que a calculadora entra na tela, conta de 0 ao valor. */
    if ('IntersectionObserver' in window && !reduceMotion) {
      var calcIo = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            calcular({ doZero: true });
            calcIo.disconnect();
          }
        });
      }, { threshold: 0.35 });
      calcIo.observe(form);
    } else {
      calcular();
    }
  }

  /* ---------- Ano no rodapé ---------- */
  var ano = document.getElementById('ano');
  if (ano) { ano.textContent = String(new Date().getFullYear()); }
})();
