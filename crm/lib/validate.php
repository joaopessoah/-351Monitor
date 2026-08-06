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
