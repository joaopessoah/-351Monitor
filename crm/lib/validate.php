<?php
/**
 * Normalização e validação de entrada.
 * Convenção de retorno dos norm_*: valor normalizado | null (vazio) | false (inválido).
 */

if (!defined('CRM')) {
    http_response_code(403);
    exit;
}

function norm_email(?string $s)
{
    $s = mb_strtolower(trim((string) $s));
    if ($s === '') {
        return null;
    }
    return filter_var($s, FILTER_VALIDATE_EMAIL) ? $s : false;
}

/** WhatsApp normalizado para só-dígitos com DDI 55 (formato aceito pelo wa.me). */
function norm_whatsapp(?string $s)
{
    $d = preg_replace('/\D+/', '', (string) $s);
    if ($d === '') {
        return null;
    }
    if (strlen($d) === 10 || strlen($d) === 11) { // veio como DDD + número
        $d = '55' . $d;
    }
    if ((strlen($d) === 12 || strlen($d) === 13) && str_starts_with($d, '55')) {
        return $d;
    }
    return false;
}

/**
 * CNPJ: 14 posições — desde jul/2026 as 12 primeiras podem ser alfanuméricas,
 * os 2 dígitos verificadores continuam numéricos (valor do char = ASCII - 48).
 */
function norm_cnpj(?string $s)
{
    $c = strtoupper(preg_replace('/[^0-9A-Za-z]+/', '', (string) $s));
    if ($c === '') {
        return null;
    }
    if (!preg_match('/^[0-9A-Z]{12}[0-9]{2}$/', $c) || preg_match('/^(.)\1{13}$/', $c)) {
        return false;
    }
    $dv = function (string $base): int {
        $sum = 0;
        $peso = 2;
        for ($i = strlen($base) - 1; $i >= 0; $i--) {
            $sum += (ord($base[$i]) - 48) * $peso;
            $peso = $peso === 9 ? 2 : $peso + 1;
        }
        $r = $sum % 11;
        return $r < 2 ? 0 : 11 - $r;
    };
    if ((int) $c[12] !== $dv(substr($c, 0, 12)) || (int) $c[13] !== $dv(substr($c, 0, 13))) {
        return false;
    }
    return $c;
}

function norm_int($v, int $min, int $max)
{
    if ($v === null || $v === '') {
        return null;
    }
    $n = filter_var($v, FILTER_VALIDATE_INT);
    if ($n === false || $n < $min || $n > $max) {
        return false;
    }
    return $n;
}

function norm_text(?string $s, int $maxLen): string
{
    $s = trim((string) $s);
    return mb_strlen($s) > $maxLen ? mb_substr($s, 0, $maxLen) : $s;
}

/** Input datetime-local ("2026-08-06T14:30") → "2026-08-06 14:30:00". */
function norm_dtlocal(?string $s)
{
    $s = trim((string) $s);
    if ($s === '') {
        return null;
    }
    if (preg_match('/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})(?::\d{2})?$/', $s, $m)) {
        return $m[1] . ' ' . $m[2] . ':00';
    }
    return false;
}

/** Datas vindas da API: aceita "YYYY-MM-DD HH:MM[:SS]" ou ISO com T (offset é ignorado — fuso fixo -03:00). */
function norm_dt_api(?string $s)
{
    $s = trim((string) $s);
    if ($s === '') {
        return null;
    }
    if (preg_match('/^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2})(?::(\d{2}))?/', $s, $m)) {
        return $m[1] . ' ' . $m[2] . ':' . ($m[3] ?? '00');
    }
    return false;
}

function in_enum(?string $v, array $allowed, string $default): string
{
    return in_array($v, $allowed, true) ? $v : $default;
}

/** URL http(s) normalizada (esquema adicionado se faltar). null vazio; false inválida. */
function norm_url(?string $s)
{
    $s = trim((string) $s);
    if ($s === '') {
        return null;
    }
    if (!preg_match('~^https?://~i', $s)) {
        $s = 'https://' . $s;
    }
    if (mb_strlen($s) > 190 || !filter_var($s, FILTER_VALIDATE_URL)) {
        return false;
    }
    return $s;
}
