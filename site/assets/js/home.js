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

  /* ---------- Calculadora: impacto do tempo ---------- */
  var form = document.getElementById('calc');
  if (form) {
    var inColab = document.getElementById('calc-colab');
    var inDias = document.getElementById('calc-dias');
    var outValue = document.getElementById('calc-value');
    var outSentence = document.getElementById('calc-sentence');
    var fmt = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });

    function clamp(v, min, max, fallback) {
      v = parseInt(v, 10);
      if (isNaN(v)) { return fallback; }
      return Math.min(max, Math.max(min, v));
    }

    function calcular(pop) {
      var colab = clamp(inColab.value, 1, 100000, 100);
      var dias = clamp(inDias.value, 1, 31, 22);
      var minutos = parseInt((form.querySelector('input[name="minutos"]:checked') || {}).value, 10) || 30;
      var horas = Math.round(colab * minutos / 60 * dias);
      var horasFmt = fmt.format(horas);

      outValue.innerHTML = horasFmt + ' horas<small> por mês</small>';
      outSentence.textContent = 'Este período representa aproximadamente ' + horasFmt +
        ' horas por mês quando considerado em toda a operação.';

      if (pop && !reduceMotion) {
        outValue.classList.remove('pop');
        void outValue.offsetWidth; /* reinicia a animação */
        outValue.classList.add('pop');
      }
    }

    form.addEventListener('submit', function (e) {
      e.preventDefault();
      calcular(true);
    });
    form.addEventListener('input', function () { calcular(false); });
    calcular(false);
  }

  /* ---------- Ano no rodapé ---------- */
  var ano = document.getElementById('ano');
  if (ano) { ano.textContent = String(new Date().getFullYear()); }
})();
