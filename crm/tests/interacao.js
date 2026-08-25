/* Exercita o crm.js real num DOM mínimo: o resumo se auto-preenche na
   cadência e nunca sobrescreve o que a pessoa digitou. */
const fs = require('fs');
const vm = require('vm');

let falhas = 0, total = 0;
const check = (cond, msg) => { total++; if (!cond) { falhas++; console.log('  FALHOU: ' + msg); } };

function el(extra = {}) {
  const e = {
    _l: {},
    hidden: false,
    value: '',
    classList: { add() {}, remove() {}, contains() { return false; }, toggle() {} },
    dataset: {},
    addEventListener(t, fn) { (this._l[t] = this._l[t] || []).push(fn); },
    dispara(t) { (this._l[t] || []).forEach(fn => fn({ target: this })); },
    querySelector() { return null; },
    querySelectorAll() { return []; },
    closest() { return null; },
    ...extra,
  };
  return e;
}

const intType = el({ value: 'whatsapp' });
const seqWrap = el({ hidden: true });
const intSeq = el({
  selectedIndex: 0,
  options: [
    { text: 'Primeiro e-mail' }, { text: 'Segundo e-mail' }, { text: 'Terceiro e-mail' },
    { text: 'Quarto e-mail' }, { text: 'Quinto e-mail' },
  ],
});
const intResumo = el({ value: '' });

const porId = {
  'int-type': intType,
  'int-email-seq-wrap': seqWrap,
  'int-email-seq': intSeq,
  'int-summary': intResumo,
};

const document = {
  getElementById: id => porId[id] || null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
};
const window = {
  matchMedia: () => ({ matches: false }),
  alert: () => {},
  confirm: () => true,
  prompt: () => null,
  location: { reload() {} },
};

const src = fs.readFileSync(__dirname + '/../assets/crm.js', 'utf8');
vm.runInNewContext(src, { document, window, URLSearchParams, fetch: () => {}, console });

console.log('== estado inicial (tipo = whatsapp) ==');
check(seqWrap.hidden === true, 'o campo "qual e-mail" deveria nascer escondido');
check(intResumo.value === '', 'o resumo nao deveria vir preenchido fora do e-mail');

console.log('== muda para e-mail ==');
intType.value = 'email';
intType.dispara('change');
check(seqWrap.hidden === false, 'o campo "qual e-mail" nao apareceu');
check(intResumo.value === 'Primeiro e-mail enviado (modelo padrão).',
  'resumo nao preenchido: "' + intResumo.value + '"');

console.log('== troca a etapa ==');
intSeq.selectedIndex = 2;
intSeq.dispara('change');
check(intResumo.value === 'Terceiro e-mail enviado (modelo padrão).',
  'resumo nao acompanhou a etapa: "' + intResumo.value + '"');

console.log('== o que a pessoa digitou e sagrado ==');
intResumo.value = 'Respondeu pedindo proposta para 40 estações.';
intSeq.selectedIndex = 3;
intSeq.dispara('change');
check(intResumo.value === 'Respondeu pedindo proposta para 40 estações.',
  'SOBRESCREVEU o texto do usuario: "' + intResumo.value + '"');
intType.value = 'ligacao';
intType.dispara('change');
check(intResumo.value === 'Respondeu pedindo proposta para 40 estações.',
  'apagou o texto do usuario ao trocar de tipo');

console.log('== texto automatico some ao sair do e-mail ==');
intResumo.value = '';
intType.value = 'email';
intType.dispara('change');
check(intResumo.value.startsWith('Quarto e-mail'), 'nao repreencheu campo vazio');
intType.value = 'whatsapp';
intType.dispara('change');
check(intResumo.value === '',
  'o texto de e-mail ficou pendurado numa interacao de whatsapp: "' + intResumo.value + '"');
check(seqWrap.hidden === true, 'o campo "qual e-mail" nao voltou a se esconder');

console.log('');
console.log(falhas === 0 ? `TODOS OS ${total} TESTES PASSARAM` : `${falhas} FALHAS de ${total}`);
process.exit(falhas === 0 ? 0 : 1);
